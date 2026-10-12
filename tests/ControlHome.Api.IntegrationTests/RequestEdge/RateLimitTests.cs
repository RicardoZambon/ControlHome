using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using ControlHome.Application.Auth;
using ControlHome.TestSupport;

namespace ControlHome.Api.IntegrationTests.RequestEdge;

/// <summary>
/// The shipped rate limits (<c>login</c>: 10/min, <c>setup</c>: 5 per 10 min, per IPv4 address or IPv6 /64) answer 429
/// <c>request.rate_limited</c> with <c>Retry-After</c>. Each test uses its own client addresses: the limiter partitions live
/// as long as the factory.
/// </summary>
public sealed class RateLimitTests(RequestEdgeApiFactory factory) : IClassFixture<RequestEdgeApiFactory>
{
    private const int LoginLimit = 10;
    private const int SetupLimit = 5;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_eleventh_login_in_a_minute_is_429_and_other_clients_are_unaffected()
    {
        using var client = factory.CreateClient();
        for (var i = 0; i < LoginLimit; i++)
        {
            using var allowed = await SendAsync(client, Login($"t1-nobody{i}"), "192.168.50.1");
            allowed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, $"login {i + 1}");
        }

        using (var limited = await SendAsync(client, Login("t1-nobody-last"), "192.168.50.1"))
        {
            await ShouldBeRateLimitedAsync(limited, maxSeconds: 60);
        }

        using var otherClient = await SendAsync(client, Login("t1-nobody-other"), "192.168.50.2");
        otherClient.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_reset_password_and_password_change_share_one_budget()
    {
        using var client = factory.CreateClient();
        for (var i = 0; i < LoginLimit; i++)
        {
            using var allowed = await SendAsync(client, Login($"t2-nobody{i}"), "192.168.50.3");
            allowed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var reset = await SendAsync(
            client,
            () => Post("/api/auth/reset-password", new ResetPasswordWithTokenRequest("nobody", "token", "a long new password")),
            "192.168.50.3");
        using var change = await SendAsync(
            client,
            () => Post("/api/auth/me/password", new ChangePasswordRequest("current password", "a long new password")),
            "192.168.50.3");

        await ShouldBeRateLimitedAsync(reset, maxSeconds: 60);
        await ShouldBeRateLimitedAsync(change, maxSeconds: 60);
    }

    [Fact]
    public async Task Password_changes_alone_are_limited_too()
    {
        using var client = factory.CreateClient();
        for (var i = 0; i < LoginLimit; i++)
        {
            // Anonymous: the limiter runs before authentication, so every attempt counts.
            using var allowed = await SendAsync(
                client,
                () => Post("/api/auth/me/password", new ChangePasswordRequest("current password", "a long new password")),
                "192.168.50.4");
            allowed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var limited = await SendAsync(
            client,
            () => Post("/api/auth/me/password", new ChangePasswordRequest("current password", "a long new password")),
            "192.168.50.4");
        await ShouldBeRateLimitedAsync(limited, maxSeconds: 60);
    }

    [Fact]
    public async Task Addresses_in_one_ipv6_64_share_a_budget()
    {
        using var client = factory.CreateClient();
        for (var i = 0; i < LoginLimit; i++)
        {
            // Privacy addresses: a fresh interface id per attempt, same /64.
            using var allowed = await SendAsync(client, Login($"t4-nobody{i}"), $"fd00:50::{i + 1:x}");
            allowed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var limited = await SendAsync(client, Login("t4-nobody-last"), "fd00:50::abcd"))
        {
            await ShouldBeRateLimitedAsync(limited, maxSeconds: 60);
        }

        using var otherNetwork = await SendAsync(client, Login("t4-nobody-other"), "fd00:50:0:1::1");
        otherNetwork.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_sixth_setup_attempt_in_ten_minutes_is_429()
    {
        using var client = factory.CreateClient();
        for (var i = 0; i < SetupLimit; i++)
        {
            using var wrong = await SendAsync(client, () => Post("/api/setup/complete", AuthTestExtensions.SetupRequest($"WRONG-CODE-{i}")), "192.168.50.5");
            wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await wrong.ReadProblemCodeAsync(Ct)).ShouldBe("setup.invalid_code");
        }

        using (var limited = await SendAsync(client, () => Post("/api/setup/complete", AuthTestExtensions.SetupRequest("WRONG-CODE-6")), "192.168.50.5"))
        {
            await ShouldBeRateLimitedAsync(limited, maxSeconds: 600);
        }

        using var otherClient = await SendAsync(client, () => Post("/api/setup/complete", AuthTestExtensions.SetupRequest("WRONG-CODE-7")), "192.168.50.6");
        otherClient.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static Func<HttpRequestMessage> Login(string userName) =>
        () => Post("/api/auth/login", new LoginRequest(userName, "wrong password", false));

    private static HttpRequestMessage Post<T>(string path, T body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Origin", "http://localhost");
        request.Headers.Add("X-ControlHome", "1");
        return request;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, Func<HttpRequestMessage> create, string remoteIp)
    {
        using var request = create();
        request.Headers.Add(RemoteIpStartupFilter.HeaderName, remoteIp);
        return await client.SendAsync(request, Ct);
    }

    private static async Task ShouldBeRateLimitedAsync(HttpResponseMessage response, int maxSeconds)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        var problem = await response.ReadJsonAsync(Ct);
        problem.GetProperty("code").GetString().ShouldBe("request.rate_limited");
        var retryAfter = problem.GetProperty("retryAfterSeconds").GetInt32();
        retryAfter.ShouldBeInRange(1, maxSeconds);
        response.Headers.TryGetValues("Retry-After", out var header).ShouldBeTrue();
        int.Parse(header!.Single(), CultureInfo.InvariantCulture).ShouldBe(retryAfter);
    }
}

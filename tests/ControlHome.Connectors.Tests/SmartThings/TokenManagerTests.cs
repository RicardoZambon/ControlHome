using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.SmartThings.Api;
using ControlHome.Connectors.SmartThings.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace ControlHome.Connectors.Tests.SmartThings;

/// <summary>
/// The OAuth token manager against a fake token endpoint: single-flight refresh (single-use refresh tokens), the rotated
/// pair persisted as one secret before use, and no retries.
/// </summary>
public sealed class TokenManagerTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeSmartThings _fake = new();
    private readonly FakeTimeProvider _time = new(StFixtures.Now);

    public void Dispose() => _fake.Dispose();

    [Fact]
    public async Task Two_concurrent_callers_share_exactly_one_refresh()
    {
        TokenEndpoint(delay: TimeSpan.FromMilliseconds(400));
        var secrets = new MemorySecretStore();
        var manager = Manager(validFor: TimeSpan.FromHours(2), secrets); // less than 3 h left: due

        var first = manager.GetAccessTokenAsync(Ct).AsTask();
        var second = manager.GetAccessTokenAsync(Ct).AsTask();
        var tokens = await Task.WhenAll(first, second);

        tokens.ShouldBe(["access-token-0002", "access-token-0002"]);
        manager.Refreshes.ShouldBe(1);
        var requests = _fake.Requests("/oauth/token", "POST");
        requests.Count.ShouldBe(1);

        // HTTP Basic client authentication and the spent refresh token in the form.
        var request = requests[0].RequestMessage!;
        request.Headers!["Authorization"].ShouldHaveSingleItem().ShouldBe("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{FakeSmartThings.ClientId}:{FakeSmartThings.ClientSecret}")));
        request.Body!.ShouldContain("grant_type=refresh_token");
        request.Body!.ShouldContain("refresh_token=refresh-token-0001");

        // The rotated pair is ONE write (atomic), made before anyone got the new token; then the redaction copies.
        var bundle = TokenSet.Deserialize(secrets.Values[SecretKeys.Tokens]).ShouldNotBeNull();
        bundle.AccessToken.ShouldBe("access-token-0002");
        bundle.RefreshToken.ShouldBe("refresh-token-0002");
        bundle.ExpiresAtUtc.ShouldBe(StFixtures.Now + TimeSpan.FromSeconds(86399));
        secrets.Writes.Select(w => w.Key).ShouldBe([SecretKeys.Tokens, SecretKeys.AccessToken, SecretKeys.RefreshToken]);
        manager.HasUnsavedTokens.ShouldBeFalse();
    }

    [Fact]
    public async Task A_token_with_time_left_is_used_as_is_until_the_weekly_keep_alive()
    {
        TokenEndpoint();
        var manager = Manager(validFor: TimeSpan.FromHours(30), new MemorySecretStore(), age: TimeSpan.FromDays(6));

        (await manager.GetAccessTokenAsync(Ct)).ShouldBe("access-token-0001");
        _fake.Requests("/oauth/token").ShouldBeEmpty();

        // Seven days after it was issued it is refreshed anyway: an unused refresh token dies after about 30 days.
        _time.Advance(TimeSpan.FromDays(1));
        (await manager.GetAccessTokenAsync(Ct)).ShouldBe("access-token-0002");
        _fake.Requests("/oauth/token").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_rejections_of_the_same_token_refresh_once()
    {
        TokenEndpoint(delay: TimeSpan.FromMilliseconds(300));
        var manager = Manager(validFor: TimeSpan.FromHours(20), new MemorySecretStore());

        var results = await Task.WhenAll(
            manager.RefreshAfterRejectionAsync("access-token-0001", Ct).AsTask(),
            manager.RefreshAfterRejectionAsync("access-token-0001", Ct).AsTask());

        results.ShouldBe(["access-token-0002", "access-token-0002"]);
        manager.Refreshes.ShouldBe(1);

        // A rejection of a token that was already replaced does not refresh again.
        (await manager.RefreshAfterRejectionAsync("access-token-0001", Ct)).ShouldBe("access-token-0002");
        _fake.Requests("/oauth/token").Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_spent_or_revoked_grant_means_reauthorize_and_is_never_retried()
    {
        _fake.Server.Given(Request.Create().WithPath("/oauth/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400).WithHeader("Content-Type", "application/json")
                .WithBody("""{"error":"invalid_grant","error_description":"Invalid refresh token"}"""));
        var manager = Manager(validFor: TimeSpan.FromHours(1), new MemorySecretStore());

        var failure = await Should.ThrowAsync<SmartThingsApiException>(() => manager.GetAccessTokenAsync(Ct).AsTask());

        failure.Kind.ShouldBe(ConnectorErrorKind.Unauthorized);
        failure.Error.Status.ShouldBe(Domain.Devices.IntegrationStatus.NeedsReauth);
        failure.VendorCode.ShouldBe("invalid_grant");
        failure.Message.ShouldNotContain("refresh-token-0001");
        _fake.Requests("/oauth/token").Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_server_error_is_transient_and_the_refresh_is_sent_only_once()
    {
        _fake.Server.Given(Request.Create().WithPath("/oauth/token").UsingPost()).RespondWith(Response.Create().WithStatusCode(502));
        var manager = Manager(validFor: TimeSpan.FromHours(1), new MemorySecretStore());

        var failure = await Should.ThrowAsync<SmartThingsApiException>(() => manager.GetAccessTokenAsync(Ct).AsTask());

        failure.Kind.ShouldBe(ConnectorErrorKind.Transient);
        _fake.Requests("/oauth/token").Count.ShouldBe(1);
        manager.Current.RefreshToken.ShouldBe("refresh-token-0001"); // nothing rotated
    }

    [Fact]
    public async Task A_rotated_pair_that_cannot_be_saved_is_kept_and_saved_before_anything_else()
    {
        TokenEndpoint();
        var secrets = new MemorySecretStore { FailWrites = true };
        var manager = Manager(validFor: TimeSpan.FromHours(1), secrets);

        // The new pair is the only valid one: it is used even though the database is down …
        (await manager.GetAccessTokenAsync(Ct)).ShouldBe("access-token-0002");
        manager.HasUnsavedTokens.ShouldBeTrue();

        // … and saved on the next call, without a second refresh.
        secrets.FailWrites = false;
        (await manager.GetAccessTokenAsync(Ct)).ShouldBe("access-token-0002");
        manager.HasUnsavedTokens.ShouldBeFalse();
        TokenSet.Deserialize(secrets.Values[SecretKeys.Tokens])!.RefreshToken.ShouldBe("refresh-token-0002");
        _fake.Requests("/oauth/token").Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_caller_that_gives_up_does_not_lose_the_rotated_pair()
    {
        TokenEndpoint(delay: TimeSpan.FromMilliseconds(500));
        var secrets = new MemorySecretStore();
        var manager = Manager(validFor: TimeSpan.FromHours(1), secrets);
        using var giveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            var refresh = manager.GetAccessTokenAsync(giveUp.Token).AsTask();
            await refresh.WaitAsync(giveUp.Token);
        });

        // The refresh went on: its answer is read and saved.
        await WaitUntilAsync(() => secrets.Values.ContainsKey(SecretKeys.Tokens));
        TokenSet.Deserialize(secrets.Values[SecretKeys.Tokens])!.RefreshToken.ShouldBe("refresh-token-0002");
        (await manager.GetAccessTokenAsync(Ct)).ShouldBe("access-token-0002");
        _fake.Requests("/oauth/token").Count.ShouldBe(1);
    }

    private TokenManager Manager(TimeSpan validFor, MemorySecretStore secrets, TimeSpan? age = null)
    {
        var tokens = new TokenSet("access-token-0001", "refresh-token-0001", StFixtures.Now + validFor, StFixtures.Now - (age ?? TimeSpan.FromHours(1)), null, null);
        var app = new OAuthApp(FakeSmartThings.ClientId, FakeSmartThings.ClientSecret, _fake.Options().DocumentedEndpoints);
        return new TokenManager(new OAuthClient(FakeSmartThings.Http(), _time, TimeSpan.FromSeconds(30)), app, tokens, secrets, _time, NullLogger.Instance, TimeSpan.FromHours(3), TimeSpan.FromDays(7));
    }

    private void TokenEndpoint(TimeSpan? delay = null)
    {
        var body = new JsonObject
        {
            ["access_token"] = "access-token-0002",
            ["token_type"] = "bearer",
            ["refresh_token"] = "refresh-token-0002",
            ["expires_in"] = 86399,
            ["scope"] = "r:locations:* r:devices:* x:devices:*",
            ["installed_app_id"] = "00000000-0000-4000-8000-00000000a990",
        }.ToJsonString();
        var response = Response.Create().WithStatusCode(HttpStatusCode.OK).WithHeader("Content-Type", "application/json").WithBody(body);
        if (delay is { } wait)
        {
            response = response.WithDelay(wait);
        }

        _fake.Server.Given(Request.Create().WithPath("/oauth/token").UsingPost()).RespondWith(response);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
            await Task.Delay(20, Ct);
        }
    }
}

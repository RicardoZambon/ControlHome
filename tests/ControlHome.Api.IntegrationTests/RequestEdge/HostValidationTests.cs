using System.Net;
using ControlHome.Api.Security;
using ControlHome.Application.Auditing;
using ControlHome.Domain.Auditing;
using ControlHome.Infrastructure.Persistence;
using ControlHome.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Api.IntegrationTests.RequestEdge;

/// <summary>
/// DNS rebinding: a page on an attacker's name, re-pointed at the app's LAN address, sends that name as <c>Host</c> and
/// <c>Origin</c> from a household member's browser (a LAN address). Only the app's own names get through.
/// </summary>
public sealed class HostValidationTests(RequestEdgeApiFactory factory) : IClassFixture<RequestEdgeApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_rebound_same_origin_login_from_the_lan_is_403_host_denied_and_never_reaches_the_sign_in()
    {
        using var client = factory.CreateClient();
        using var request = RequestEdgeRequests.Create(
            HttpMethod.Post,
            "/api/auth/login",
            "192.168.1.23",
            ("Host", "rb.attacker.example:8711"),
            ("Origin", "http://rb.attacker.example:8711"),
            ("X-ControlHome", "1"));
        request.Content = new StringContent("""{"userName":"admin","password":"x","rememberMe":false}""", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        (await response.ReadProblemCodeAsync(Ct)).ShouldBe("request.host_denied");
        response.Headers.Contains("Content-Security-Policy").ShouldBeTrue(); // security headers still apply
        (await AuditEntriesAsync(AuditActions.LoginFailed)).ShouldBeEmpty();
        var denied = (await AuditEntriesAsync(AuditActions.HostDenied)).Where(e => e.IpAddress == "192.168.1.23").ShouldHaveSingleItem();
        denied.ActorType.ShouldBe(AuditActorType.Anonymous);
        denied.DetailsJson.ShouldNotBeNull();
        denied.DetailsJson.ShouldContain("rb.attacker.example:8711");
    }

    [Theory]
    [InlineData("GET", "/api/setup/status")] // GET responses would be readable by the rebound page
    [InlineData("GET", "/")]
    [InlineData("GET", "/health/ready")]
    [InlineData("POST", "/api/hooks/anything")] // hooks skip the origin check, not this one
    public async Task Any_method_and_path_for_a_foreign_host_is_denied(string method, string path)
    {
        using var client = factory.CreateClient();
        using var request = RequestEdgeRequests.Create(new HttpMethod(method), path, "192.168.1.30", ("Host", "evil.example"));

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadProblemCodeAsync(Ct)).ShouldBe("request.host_denied");
    }

    [Theory]
    [InlineData("192.168.1.10:8711")]
    [InlineData("[fd00::1]:8711")]
    [InlineData("umbrel.local")]
    [InlineData("Umbrel.Local.")]
    [InlineData("localhost:5080")]
    [InlineData("umbrel")]
    [InlineData("controlhome.home.arpa")]
    public async Task The_apps_own_names_are_allowed(string host)
    {
        using var client = factory.CreateClient();
        using var request = RequestEdgeRequests.Create(HttpMethod.Get, "/api/setup/status", "192.168.1.31", ("Host", host));

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("evil.example", HttpStatusCode.Forbidden)]
    [InlineData("umbrel.local", HttpStatusCode.OK)]
    public async Task The_host_a_trusted_proxy_forwards_is_the_one_checked(string forwardedHost, HttpStatusCode expected)
    {
        using var client = factory.CreateClient();
        using var request = RequestEdgeRequests.Create(
            HttpMethod.Get,
            "/api/setup/status",
            "127.0.0.1", // a loopback proxy is trusted by default
            ("Host", "localhost"),
            ("X-Forwarded-Host", forwardedHost));

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(expected);
    }

    [Fact]
    public async Task Denied_hosts_are_audited_once_per_source()
    {
        using var client = factory.CreateClient();
        foreach (var remoteIp in new[] { "192.168.1.40", "192.168.1.40", "192.168.1.40", "192.168.1.41" })
        {
            using var request = RequestEdgeRequests.Create(HttpMethod.Get, "/api/setup/status", remoteIp, ("Host", "evil.example"));
            using var response = await client.SendAsync(request, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var entries = await AuditEntriesAsync(AuditActions.HostDenied);
        entries.Count(e => e.IpAddress == "192.168.1.40").ShouldBe(1);
        entries.Count(e => e.IpAddress == "192.168.1.41").ShouldBe(1);
    }

    private async Task<List<AuditLogEntry>> AuditEntriesAsync(string action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlHomeDbContext>();
        return await db.AuditLog.AsNoTracking().Where(e => e.Action == action).ToListAsync(Ct);
    }
}

/// <summary><c>Network:AllowedHostNames</c> adds names (exact or <c>*.suffix</c>) to the always-allowed ones.</summary>
public sealed class AllowedHostNamesTests(NoDatabaseApiFactory factory) : IClassFixture<NoDatabaseApiFactory>
{
    [Theory]
    [InlineData("home.example.com", HttpStatusCode.OK)]
    [InlineData("HOME.example.com:8711", HttpStatusCode.OK)]
    [InlineData("umbrel.tail1234.ts.net", HttpStatusCode.OK)]
    [InlineData("tail1234.ts.net", HttpStatusCode.Forbidden)] // the wildcard covers names below it only
    [InlineData("example.com", HttpStatusCode.Forbidden)]
    [InlineData("evil.example", HttpStatusCode.Forbidden)]
    [InlineData("umbrel.local", HttpStatusCode.OK)] // the defaults stay
    public async Task Configured_names_are_allowed_besides_the_defaults(string host, HttpStatusCode expected)
    {
        using var configured = factory.WithWebHostBuilder(b => b.UseSetting("Network:AllowedHostNames", "home.example.com, *.tail1234.ts.net"));
        using var client = configured.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/setup/status");
        request.Headers.Host = host;

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(expected);
    }
}

/// <summary>Unit tests (no server) for <see cref="HostNamePolicy"/>.</summary>
public sealed class HostNamePolicyTests
{
    private static readonly HostNamePolicy _policy = new(["home.example.com", "*.tail1234.ts.net"]);

    [Theory]
    [InlineData("192.168.1.10")]
    [InlineData("192.168.1.10:8711")]
    [InlineData("10.0.0.5:80")]
    [InlineData("[fd00::1]:8711")]
    [InlineData("[::1]")]
    [InlineData("localhost")]
    [InlineData("LOCALHOST:5173")]
    [InlineData("app.localhost")]
    [InlineData("umbrel")]
    [InlineData("umbrel.local")]
    [InlineData("umbrel.local.")]
    [InlineData("nas.home.arpa")]
    [InlineData("router.lan")]
    [InlineData("box.internal")]
    [InlineData("home.example.com")]
    [InlineData("Home.Example.Com:443")]
    [InlineData("a.b.tail1234.ts.net")]
    public void Allows_the_apps_names(string host)
    {
        _policy.IsAllowed(new HostString(host)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("evil.example")]
    [InlineData("rb.attacker.example:8711")]
    [InlineData("192.168.1.10.nip.io")]
    [InlineData("umbrel.local.evil.example")]
    [InlineData(".local")]
    [InlineData("tail1234.ts.net")]
    [InlineData("example.com")]
    [InlineData("notlocalhost.com")]
    [InlineData("[not-an-address]")]
    public void Denies_every_other_name(string host)
    {
        _policy.IsAllowed(new HostString(host)).ShouldBeFalse();
    }

    [Fact]
    public void A_missing_host_is_denied()
    {
        _policy.IsAllowed(default).ShouldBeFalse();
    }

    [Theory]
    [InlineData("home.example.com", true)]
    [InlineData("Home.Example.Com", true)]
    [InlineData("*.tail1234.ts.net", true)]
    [InlineData("umbrel", true)]
    [InlineData("my-box.example.com.", true)]
    [InlineData("https://home.example.com", false)]
    [InlineData("home.example.com:8711", false)]
    [InlineData("home.example.com/path", false)]
    [InlineData("*", false)]
    [InlineData("*.", false)]
    [InlineData("**.example.com", false)]
    [InlineData("192.168.1.10", false)] // IP literals are always allowed; no entry needed
    [InlineData("-bad.example.com", false)]
    [InlineData("home..example.com", false)]
    [InlineData("", false)]
    public void Configuration_entries_are_host_names_or_wildcards(string entry, bool valid)
    {
        HostNamePolicy.IsValidEntry(entry).ShouldBe(valid);
    }
}

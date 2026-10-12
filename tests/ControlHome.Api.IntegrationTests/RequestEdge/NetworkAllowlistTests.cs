using System.Net;
using ControlHome.Api.Security;
using ControlHome.Application.Auditing;
using ControlHome.Domain.Auditing;
using ControlHome.Infrastructure.Persistence;
using ControlHome.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ControlHome.Api.IntegrationTests.RequestEdge;

/// <summary>
/// Only clients from <c>Network:AllowedClientNetworks</c> get through (403 <c>request.network_denied</c> otherwise, audited
/// at most once per address per 10 minutes). Each test uses its own addresses: the audit throttle lives as long as the factory.
/// </summary>
public sealed class NetworkAllowlistTests(RequestEdgeApiFactory factory) : IClassFixture<RequestEdgeApiFactory>
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("10.20.30.40")]
    [InlineData("172.20.0.5")]
    [InlineData("192.168.1.20")]
    [InlineData("100.64.1.2")]
    [InlineData("fd00::20")]
    [InlineData("fe80::20")]
    [InlineData("::ffff:192.168.1.21")]
    public async Task Local_clients_are_allowed(string remoteIp)
    {
        using var client = factory.CreateClient();
        using var request = RequestEdgeRequests.Create(HttpMethod.Get, "/api/setup/status", remoteIp);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("2001:db8::8")]
    [InlineData("::ffff:1.1.1.1")]
    public async Task Other_clients_get_403_network_denied(string remoteIp)
    {
        using var client = factory.CreateClient();
        using var request = RequestEdgeRequests.Create(HttpMethod.Get, "/api/setup/status", remoteIp);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        (await response.ReadProblemCodeAsync(TestContext.Current.CancellationToken)).ShouldBe("request.network_denied");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/health/live")]
    [InlineData("/hubs/x")]
    public async Task The_allowlist_covers_every_path(string path)
    {
        using var client = factory.CreateClient();
        using var request = RequestEdgeRequests.Create(HttpMethod.Get, path, "203.0.113.50");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadProblemCodeAsync(TestContext.Current.CancellationToken)).ShouldBe("request.network_denied");
    }

    [Fact]
    public async Task A_same_origin_mutation_from_outside_is_denied_too()
    {
        using var client = factory.CreateClient();
        using var request = RequestEdgeRequests.Create(
            HttpMethod.Post,
            "/api/_test/reset",
            "203.0.113.51",
            ("Origin", "http://localhost"),
            ("X-ControlHome", "1"));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadProblemCodeAsync(TestContext.Current.CancellationToken)).ShouldBe("request.network_denied");
    }

    [Fact]
    public async Task Requests_without_a_remote_address_are_allowed()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/setup/status", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Denied_requests_are_audited_once_per_address()
    {
        using var client = factory.CreateClient();

        foreach (var remoteIp in new[] { "198.51.100.7", "198.51.100.7", "198.51.100.7", "198.51.100.8" })
        {
            using var request = RequestEdgeRequests.Create(HttpMethod.Get, "/api/setup/status", remoteIp);
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var entries = await AuditEntriesAsync("198.51.100.7", "198.51.100.8");
        entries.Count(e => e.IpAddress == "198.51.100.7").ShouldBe(1);
        entries.Count(e => e.IpAddress == "198.51.100.8").ShouldBe(1);
        var entry = entries.First(e => e.IpAddress == "198.51.100.7");
        entry.ActorType.ShouldBe(AuditActorType.Anonymous);
        entry.DetailsJson.ShouldNotBeNull();
        entry.DetailsJson.ShouldContain("\"method\":\"GET\"");
        entry.DetailsJson.ShouldContain("\"path\":\"/api/setup/status\"");
    }

    [Fact]
    public async Task Forwarded_for_from_a_loopback_proxy_is_trusted_by_default()
    {
        using var client = factory.CreateClient();
        using var request = RequestEdgeRequests.Create(
            HttpMethod.Get,
            "/api/setup/status",
            "127.0.0.1",
            ("X-Forwarded-For", "198.51.100.20"));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AuditEntriesAsync("198.51.100.20")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Forwarded_for_from_any_other_client_is_ignored()
    {
        using var client = factory.CreateClient();
        using var request = RequestEdgeRequests.Create(
            HttpMethod.Get,
            "/api/setup/status",
            "198.51.100.30",
            ("X-Forwarded-For", "192.168.1.5"));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AuditEntriesAsync("198.51.100.30")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Origin_is_compared_with_the_forwarded_scheme_and_host()
    {
        using var client = factory.CreateClient();
        (string, string)[] forwarded = [("X-Forwarded-Proto", "https"), ("X-Forwarded-Host", "home.example.test"), ("X-ControlHome", "1")];

        using var proxied = RequestEdgeRequests.Create(
            HttpMethod.Post, "/api/_test/reset", "127.0.0.1", [.. forwarded, ("Origin", "https://home.example.test")]);
        using var direct = RequestEdgeRequests.Create(
            HttpMethod.Post, "/api/_test/reset", "127.0.0.1", [.. forwarded, ("Origin", "http://localhost")]);

        using var proxiedResponse = await client.SendAsync(proxied, TestContext.Current.CancellationToken);
        using var directResponse = await client.SendAsync(direct, TestContext.Current.CancellationToken);

        proxiedResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        directResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await directResponse.ReadProblemCodeAsync(TestContext.Current.CancellationToken)).ShouldBe("request.cross_origin");
    }

    [Fact]
    public void Shipped_configuration_uses_the_default_networks_and_framework_proxy_defaults()
    {
        var network = factory.Services.GetRequiredService<IOptions<NetworkOptions>>().Value;
        var forwarded = factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        network.AllowedClientNetworks.ShouldBe(NetworkOptions.DefaultAllowedClientNetworks);
        network.TrustedProxyNetworks.ShouldBeEmpty();
        forwarded.ForwardedHeaders.ShouldBe(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost);
        forwarded.KnownProxies.ShouldBe([IPAddress.IPv6Loopback]);
        forwarded.KnownIPNetworks.Select(n => n.ToString()).ShouldBe(["127.0.0.0/8"]);
    }

    private async Task<List<AuditLogEntry>> AuditEntriesAsync(params string[] ipAddresses)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlHomeDbContext>();
        return await db.AuditLog.AsNoTracking()
            .Where(e => e.Action == AuditActions.NetworkDenied && ipAddresses.Contains(e.IpAddress))
            .ToListAsync(TestContext.Current.CancellationToken);
    }
}

/// <summary>With <c>Network:TrustedProxyNetworks</c> set, only that subnet may forward (loopback is no longer trusted).</summary>
public sealed class TrustedProxyTests(TrustedProxyApiFactory factory) : IClassFixture<TrustedProxyApiFactory>
{
    [Theory]
    [InlineData("172.30.0.2", "8.8.8.8", HttpStatusCode.Forbidden)] // the proxy forwards an outside client
    [InlineData("172.30.0.2", "192.168.1.5", HttpStatusCode.OK)] // the proxy forwards a LAN client
    [InlineData("127.0.0.1", "8.8.8.8", HttpStatusCode.OK)] // loopback is not a trusted proxy any more: header ignored
    [InlineData("192.168.1.9", "8.8.8.8", HttpStatusCode.OK)] // nor is any LAN client
    public async Task Forwarded_for_is_trusted_only_from_the_configured_networks(string remoteIp, string forwardedFor, HttpStatusCode expected)
    {
        using var client = factory.CreateClient();
        using var request = RequestEdgeRequests.Create(HttpMethod.Get, "/api/setup/status", remoteIp, ("X-Forwarded-For", forwardedFor));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(expected);
    }

    [Fact]
    public void Configured_networks_replace_the_framework_defaults()
    {
        var forwarded = factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        forwarded.KnownProxies.ShouldBeEmpty();
        forwarded.KnownIPNetworks.Select(n => n.ToString()).ShouldBe([TrustedProxyApiFactory.ProxyNetwork]);
    }
}

public sealed class InvalidNetworkConfigurationTests(NoDatabaseApiFactory factory) : IClassFixture<NoDatabaseApiFactory>
{
    [Theory]
    [InlineData("Network:AllowedClientNetworks", "192.168/16")]
    [InlineData("Network:TrustedProxyNetworks", "proxy.local")]
    public void Startup_fails_with_a_message_naming_the_setting(string key, string value)
    {
        using var invalid = factory.WithWebHostBuilder(b => b.UseSetting(key, value));

        var thrown = Should.Throw<Exception>(() => invalid.CreateClient());

        var validation = Flatten(thrown).OfType<OptionsValidationException>().FirstOrDefault();
        validation.ShouldNotBeNull(thrown.ToString());
        validation.Message.ShouldContain(key);
        validation.Message.ShouldContain($"'{value}'");
    }

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        yield return exception;
        IEnumerable<Exception> children = exception switch
        {
            AggregateException aggregate => aggregate.InnerExceptions,
            { InnerException: { } inner } => [inner],
            _ => [],
        };
        foreach (var descendant in children.SelectMany(Flatten))
        {
            yield return descendant;
        }
    }
}

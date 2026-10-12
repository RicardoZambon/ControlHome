using System.Net;
using ControlHome.Api.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace ControlHome.Api.IntegrationTests.RequestEdge;

/// <summary>Unit tests (no server, no database) for the network allowlist building blocks.</summary>
public sealed class IpNetworkParserTests
{
    [Theory]
    [InlineData("10.0.0.0/8", "10.0.0.0/8")]
    [InlineData(" 192.168.1.0/24 ", "192.168.1.0/24")]
    [InlineData("192.168.1.7", "192.168.1.7/32")]
    [InlineData("10.1.2.3/8", "10.0.0.0/8")] // host bits are cleared
    [InlineData("0.0.0.0/0", "0.0.0.0/0")]
    [InlineData("::1", "::1/128")]
    [InlineData("::1/128", "::1/128")]
    [InlineData("fc00::/7", "fc00::/7")]
    [InlineData("FE80::/10", "fe80::/10")]
    [InlineData("2001:db8::1/64", "2001:db8::/64")]
    [InlineData("::ffff:192.168.0.0/112", "192.168.0.0/16")] // IPv4-mapped is the IPv4 network
    [InlineData("::ffff:10.0.0.1", "10.0.0.1/32")]
    public void Parses_addresses_and_cidr_networks(string text, string expected)
    {
        IpNetworkParser.TryParse(text, out var network).ShouldBeTrue();

        network.ToString().ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nope")]
    [InlineData("192.168/16")] // legacy short forms would silently mean 192.0.0.168
    [InlineData("10")]
    [InlineData("010.0.0.1")] // octal
    [InlineData("0x0a.0.0.1")]
    [InlineData("::ffff:192.168.0.0/16")] // an IPv6 prefix shorter than the mapped range
    [InlineData("10.0.0.0/33")]
    [InlineData("::/129")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("10.0.0.0/")]
    [InlineData("10.0.0.0/8/9")]
    [InlineData("10.0.0.0/ 8")]
    [InlineData("10.0.0.0/abc")]
    [InlineData("fe80::1%eth0/64")]
    public void Rejects_anything_else(string text)
    {
        IpNetworkParser.TryParse(text, out _).ShouldBeFalse();
    }

    [Fact]
    public void ParseAll_names_the_invalid_entry()
    {
        var exception = Should.Throw<FormatException>(() => IpNetworkParser.ParseAll(["10.0.0.0/8", "192.168/16"]));

        exception.Message.ShouldContain("192.168/16");
    }
}

public sealed class ClientNetworkPolicyTests
{
    private static readonly ClientNetworkPolicy _defaults =
        new(IpNetworkParser.ParseAll(NetworkOptions.DefaultAllowedClientNetworks));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.10.20.30")]
    [InlineData("::1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.20")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("fd12:3456::1")]
    [InlineData("fe80::1")]
    [InlineData("fe80::1%3")]
    [InlineData("::ffff:192.168.1.20")]
    public void Default_networks_allow_local_addresses(string address)
    {
        _defaults.IsAllowed(IPAddress.Parse(address)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("172.15.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("192.169.0.1")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:8.8.8.8")]
    public void Default_networks_deny_public_addresses(string address)
    {
        _defaults.IsAllowed(IPAddress.Parse(address)).ShouldBeFalse();
    }

    [Fact]
    public void An_unknown_remote_address_is_allowed()
    {
        // TestServer and Unix sockets have no remote IP.
        _defaults.IsAllowed(null).ShouldBeTrue();
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void Loopback_is_always_allowed(string address)
    {
        // The container healthcheck calls the app on loopback, whatever the allowlist says.
        var policy = new ClientNetworkPolicy(IpNetworkParser.ParseAll(["192.168.1.0/24"]));

        policy.IsAllowed(IPAddress.Parse(address)).ShouldBeTrue();
    }

    [Fact]
    public void A_configured_list_replaces_the_defaults()
    {
        var policy = new ClientNetworkPolicy(IpNetworkParser.ParseAll(["192.168.1.0/24"]));

        policy.IsAllowed(IPAddress.Parse("192.168.1.99")).ShouldBeTrue();
        policy.IsAllowed(IPAddress.Parse("192.168.2.1")).ShouldBeFalse();
        policy.IsAllowed(IPAddress.Parse("10.0.0.1")).ShouldBeFalse();
    }
}

public sealed class NetworkDeniedAuditThrottleTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Reports_an_address_once_per_ten_minutes()
    {
        var throttle = new NetworkDeniedAuditThrottle(_time);
        var address = IPAddress.Parse("8.8.8.8");

        throttle.TryAcquire(address).ShouldBeTrue();
        throttle.TryAcquire(address).ShouldBeFalse();
        _time.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        throttle.TryAcquire(address).ShouldBeFalse();
        _time.Advance(TimeSpan.FromSeconds(1));
        throttle.TryAcquire(address).ShouldBeTrue();
        throttle.TryAcquire(address).ShouldBeFalse();
    }

    [Fact]
    public void Each_address_has_its_own_window()
    {
        var throttle = new NetworkDeniedAuditThrottle(_time);

        throttle.TryAcquire(IPAddress.Parse("8.8.8.8")).ShouldBeTrue();
        throttle.TryAcquire(IPAddress.Parse("8.8.4.4")).ShouldBeTrue();
        throttle.TryAcquire(IPAddress.Parse("8.8.4.4")).ShouldBeFalse();
    }

    [Fact]
    public void Ipv4_mapped_addresses_count_as_the_ipv4_address()
    {
        var throttle = new NetworkDeniedAuditThrottle(_time);

        throttle.TryAcquire(IPAddress.Parse("8.8.8.8")).ShouldBeTrue();
        throttle.TryAcquire(IPAddress.Parse("::ffff:8.8.8.8")).ShouldBeFalse();
    }

    [Fact]
    public void Ipv6_addresses_are_grouped_by_their_64_bit_prefix()
    {
        // One host usually owns a whole /64, so per-address throttling would not limit it at all.
        var throttle = new NetworkDeniedAuditThrottle(_time);

        throttle.TryAcquire(IPAddress.Parse("2001:db8:1:2::1")).ShouldBeTrue();
        throttle.TryAcquire(IPAddress.Parse("2001:db8:1:2:ffff::9")).ShouldBeFalse();
        throttle.TryAcquire(IPAddress.Parse("2001:db8:1:3::1")).ShouldBeTrue();
    }

    [Fact]
    public void Tracks_a_bounded_number_of_sources_and_frees_expired_ones()
    {
        var throttle = new NetworkDeniedAuditThrottle(_time);
        for (var i = 0; i < NetworkDeniedAuditThrottle.MaxTrackedSources; i++)
        {
            throttle.TryAcquire(new IPAddress([11, 0, (byte)(i >> 8), (byte)i])).ShouldBeTrue();
        }

        // Full: a new source is not reported (bounding audit rows per window), ...
        throttle.TryAcquire(IPAddress.Parse("9.9.9.9")).ShouldBeFalse();

        // ... until the window has passed for the tracked ones.
        _time.Advance(NetworkDeniedAuditThrottle.Window);
        throttle.TryAcquire(IPAddress.Parse("9.9.9.9")).ShouldBeTrue();
    }
}

public sealed class NetworkOptionsTests
{
    [Fact]
    public void Missing_section_keeps_the_defaults()
    {
        var options = Bind([]);

        options.AllowedClientNetworks.ShouldBe(NetworkOptions.DefaultAllowedClientNetworks);
        options.TrustedProxyNetworks.ShouldBeEmpty();
    }

    [Fact]
    public void Default_allowed_networks_are_loopback_private_cgnat_ula_and_link_local()
    {
        NetworkOptions.DefaultAllowedClientNetworks.ShouldBe(
        [
            "127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "100.64.0.0/10", "fc00::/7", "fe80::/10",
        ]);
    }

    [Fact]
    public void A_comma_separated_value_replaces_the_list()
    {
        var options = Bind(new()
        {
            ["Network:AllowedClientNetworks"] = "192.168.1.0/24, 10.8.0.0/16;fd00::/8",
            ["Network:TrustedProxyNetworks"] = "172.30.0.0/16",
        });

        options.AllowedClientNetworks.ShouldBe(["192.168.1.0/24", "10.8.0.0/16", "fd00::/8"]);
        options.TrustedProxyNetworks.ShouldBe(["172.30.0.0/16"]);
    }

    [Fact]
    public void An_array_is_read_too_and_wins_over_a_scalar_value()
    {
        // appsettings.json holds a comma-separated string; Network__AllowedClientNetworks__0=... style overrides still work.
        var options = Bind(new()
        {
            ["Network:AllowedClientNetworks"] = "127.0.0.0/8, 10.0.0.0/8",
            ["Network:AllowedClientNetworks:0"] = "192.168.1.0/24",
            ["Network:AllowedClientNetworks:1"] = "192.168.2.0/24",
        });

        options.AllowedClientNetworks.ShouldBe(["192.168.1.0/24", "192.168.2.0/24"]);
    }

    [Fact]
    public void An_empty_value_is_an_empty_list()
    {
        var options = Bind(new() { ["Network:TrustedProxyNetworks"] = "", ["Network:AllowedClientNetworks"] = " " });

        options.TrustedProxyNetworks.ShouldBeEmpty();
        options.AllowedClientNetworks.ShouldBeEmpty();
    }

    [Fact]
    public void Validation_names_each_invalid_entry()
    {
        var options = new NetworkOptions { AllowedClientNetworks = ["10.0.0.0/8", "nope"], TrustedProxyNetworks = ["172.30.0.0/33"] };

        var result = new NetworkOptionsValidator().Validate(Options.DefaultName, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Network:AllowedClientNetworks");
        result.FailureMessage.ShouldContain("'nope'");
        result.FailureMessage.ShouldContain("Network:TrustedProxyNetworks");
        result.FailureMessage.ShouldContain("'172.30.0.0/33'");
    }

    [Fact]
    public void Validation_accepts_the_defaults()
    {
        new NetworkOptionsValidator().Validate(Options.DefaultName, new NetworkOptions()).Succeeded.ShouldBeTrue();
    }

    private static NetworkOptions Bind(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var options = new NetworkOptions();
        options.Bind(configuration.GetSection(NetworkOptions.SectionName));
        return options;
    }
}

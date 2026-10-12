using Microsoft.Extensions.Options;

namespace ControlHome.Api.Security;

/// <summary>
/// Who may talk to the app (configuration section <c>Network</c>). Each list is a comma-separated string
/// (<c>Network__AllowedClientNetworks=192.168.1.0/24, fd00::/8</c>) or an array; entries are IP addresses or CIDR networks.
/// A missing setting keeps the default.
/// </summary>
public sealed class NetworkOptions
{
    public const string SectionName = "Network";

    /// <summary>Loopback, RFC 1918, CGNAT (Tailscale), IPv6 ULA and link-local.</summary>
    public static IReadOnlyList<string> DefaultAllowedClientNetworks { get; } =
    [
        "127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "100.64.0.0/10", "fc00::/7", "fe80::/10",
    ];

    /// <summary>
    /// Client networks allowed to use the app (after forwarded headers); others get 403 <c>request.network_denied</c>.
    /// Loopback is always allowed (the container healthcheck), whatever this list says.
    /// </summary>
    public IReadOnlyList<string> AllowedClientNetworks { get; set; } = DefaultAllowedClientNetworks;

    /// <summary>
    /// Reverse proxies (e.g. Umbrel's app_proxy subnet) whose <c>X-Forwarded-For/Proto/Host</c> headers are applied. Empty
    /// (default): the framework default, loopback proxies only. When set, it replaces that default.
    /// </summary>
    public IReadOnlyList<string> TrustedProxyNetworks { get; set; } = [];

    /// <summary>Reads <paramref name="section"/> (the <c>Network</c> section) into this instance.</summary>
    public void Bind(IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(section);
        AllowedClientNetworks = ReadList(section.GetSection(nameof(AllowedClientNetworks))) ?? AllowedClientNetworks;
        TrustedProxyNetworks = ReadList(section.GetSection(nameof(TrustedProxyNetworks))) ?? TrustedProxyNetworks;
    }

    /// <summary>Array entries if any (each may itself be a list), else the comma-separated value, else <c>null</c> (missing).</summary>
    private static string[]? ReadList(IConfigurationSection section)
    {
        var children = section.GetChildren().ToList();
        if (children.Count > 0)
        {
            return [.. children.SelectMany(child => Split(child.Value))];
        }

        return section.Value is null ? null : Split(section.Value);
    }

    private static string[] Split(string? value) =>
        (value ?? string.Empty).Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Every entry must be an IP address or a CIDR network; the failure names the setting and the entry.</summary>
internal sealed class NetworkOptionsValidator : IValidateOptions<NetworkOptions>
{
    public ValidateOptionsResult Validate(string? name, NetworkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = Invalid(nameof(NetworkOptions.AllowedClientNetworks), options.AllowedClientNetworks)
            .Concat(Invalid(nameof(NetworkOptions.TrustedProxyNetworks), options.TrustedProxyNetworks))
            .ToList();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static IEnumerable<string> Invalid(string setting, IEnumerable<string> entries) =>
        entries
            .Where(entry => !IpNetworkParser.TryParse(entry, out _))
            .Select(entry =>
                $"{NetworkOptions.SectionName}:{setting}: '{entry}' is not an IP address or CIDR network (e.g. 192.168.1.0/24, fd00::/8).");
}

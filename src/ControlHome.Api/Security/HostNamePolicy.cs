using System.Net;
using System.Net.Sockets;

namespace ControlHome.Api.Security;

/// <summary>
/// Which <c>Host</c> names the app answers to. A DNS rebinding page (an attacker's name re-pointed at the app's LAN
/// address) sends its own name as <c>Host</c> and as <c>Origin</c>, so the same-origin check alone would let it through;
/// it cannot serve a page from a name that public DNS does not resolve. Always allowed: IP literals, <c>localhost</c>,
/// single-label names (<c>umbrel</c>) and names under <see cref="LocalSuffixes"/>; plus every entry of
/// <see cref="NetworkOptions.AllowedHostNames"/> (an exact name, or <c>*.suffix</c> for any name below it). Names compare
/// case-insensitively; a trailing root dot is ignored.
/// </summary>
internal sealed class HostNamePolicy
{
    /// <summary>Names that only resolve inside the home network (mDNS, RFC 8375, common router and private-use suffixes).</summary>
    public static IReadOnlyList<string> LocalSuffixes { get; } = [".localhost", ".local", ".home.arpa", ".lan", ".internal"];

    private const int MaxHostNameLength = 253;

    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _suffixes = [.. LocalSuffixes];

    public HostNamePolicy(IEnumerable<string> allowedHostNames)
    {
        ArgumentNullException.ThrowIfNull(allowedHostNames);
        foreach (var entry in allowedHostNames)
        {
            var name = Trim(entry);
            if (name.StartsWith("*.", StringComparison.Ordinal))
            {
                _suffixes.Add(name[1..]);
            }
            else
            {
                _names.Add(name);
            }
        }
    }

    /// <summary>Whether a request for <paramref name="host"/> (port ignored) may be served.</summary>
    public bool IsAllowed(HostString host)
    {
        if (!host.HasValue)
        {
            return false;
        }

        var name = host.Host;
        if (name.Length == 0 || name.Length > MaxHostNameLength)
        {
            return false;
        }

        if (name.StartsWith('[') && name.EndsWith(']'))
        {
            return IPAddress.TryParse(name.AsSpan(1, name.Length - 2), out var address)
                && address.AddressFamily == AddressFamily.InterNetworkV6;
        }

        if (IPAddress.TryParse(name, out _))
        {
            return true;
        }

        name = Trim(name);
        if (name.Length == 0)
        {
            return false;
        }

        if (string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase)
            || !name.Contains('.', StringComparison.Ordinal)
            || _names.Contains(name))
        {
            return true;
        }

        foreach (var suffix in _suffixes)
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A configuration entry: a DNS name (letters, digits, hyphens, dots) or <c>*.</c> followed by one.</summary>
    public static bool IsValidEntry(string? entry)
    {
        var name = Trim(entry);
        if (name.StartsWith("*.", StringComparison.Ordinal))
        {
            name = name[2..];
        }

        if (name.Length == 0 || name.Length > MaxHostNameLength || IPAddress.TryParse(name, out _))
        {
            return false;
        }

        return name.Split('.').All(label =>
            label.Length is > 0 and <= 63
            && label[0] != '-'
            && label[^1] != '-'
            && label.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
    }

    /// <summary>Without surrounding white space and the root dot (<c>umbrel.local.</c> → <c>umbrel.local</c>).</summary>
    private static string Trim(string? value) => (value ?? string.Empty).Trim().TrimEnd('.');
}

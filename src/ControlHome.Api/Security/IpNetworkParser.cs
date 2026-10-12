using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace ControlHome.Api.Security;

/// <summary>Parses configuration entries such as <c>192.168.1.0/24</c>, <c>fd00::/8</c> or a single address.</summary>
internal static class IpNetworkParser
{
    /// <summary>
    /// Accepts <c>address</c> or <c>address/prefix</c>; host bits after the prefix are cleared (<c>10.1.2.3/8</c> is
    /// <c>10.0.0.0/8</c>); IPv4-mapped IPv6 becomes IPv4 (<c>::ffff:192.168.0.0/112</c> is <c>192.168.0.0/16</c>). IPv4
    /// must be canonical dotted-quad, and scope ids are not allowed.
    /// </summary>
    public static bool TryParse(string? text, out IPNetwork network)
    {
        network = default;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var slash = value.IndexOf('/', StringComparison.Ordinal);
        var addressText = slash < 0 ? value : value[..slash];
        if (addressText.Contains('%', StringComparison.Ordinal) || !IPAddress.TryParse(addressText, out var address))
        {
            return false;
        }

        // IPv4 only in canonical dotted-quad form: IPAddress also parses "192.168" (192.0.0.168) and "010.0.0.1" (octal).
        if (address.AddressFamily == AddressFamily.InterNetwork
            && !string.Equals(address.ToString(), addressText, StringComparison.Ordinal))
        {
            return false;
        }

        var prefixOffset = 0;
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
            prefixOffset = 96;
        }

        var maxPrefix = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = maxPrefix;
        if (slash >= 0)
        {
            if (!int.TryParse(value.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                return false;
            }

            prefix = parsed - prefixOffset;
            if (prefix < 0 || prefix > maxPrefix)
            {
                return false;
            }
        }

        network = new IPNetwork(Mask(address, prefix), prefix);
        return true;
    }

    /// <summary>Parses every entry; throws <see cref="FormatException"/> naming the first invalid one.</summary>
    public static IReadOnlyList<IPNetwork> ParseAll(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var networks = new List<IPNetwork>();
        foreach (var entry in entries)
        {
            if (!TryParse(entry, out var network))
            {
                throw new FormatException($"'{entry}' is not an IP address or CIDR network.");
            }

            networks.Add(network);
        }

        return networks;
    }

    private static IPAddress Mask(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bits = prefix - (i * 8);
            bytes[i] &= bits switch
            {
                >= 8 => 0xFF,
                <= 0 => 0x00,
                _ => (byte)(0xFF << (8 - bits)),
            };
        }

        return new IPAddress(bytes);
    }
}

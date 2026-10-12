using System.Net;
using System.Net.Sockets;

namespace ControlHome.Api.Security;

/// <summary>Decides whether a client address may use the app (<see cref="NetworkOptions.AllowedClientNetworks"/>).</summary>
internal sealed class ClientNetworkPolicy(IEnumerable<IPNetwork> allowedNetworks)
{
    private readonly IPNetwork[] _allowedNetworks = [.. allowedNetworks];

    /// <summary>
    /// True for an unknown address (TestServer, Unix sockets), for loopback and for an address inside an allowed network.
    /// IPv4-mapped IPv6 addresses are checked as IPv4.
    /// </summary>
    public bool IsAllowed(IPAddress? address)
    {
        address = Normalize(address);
        if (address is null || IPAddress.IsLoopback(address))
        {
            return true;
        }

        foreach (var network in _allowedNetworks)
        {
            if (network.Contains(address))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><c>::ffff:a.b.c.d</c> → <c>a.b.c.d</c>; anything else unchanged.</summary>
    public static IPAddress? Normalize(IPAddress? address) =>
        address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;

    /// <summary>
    /// The client "source" an address belongs to, for per-client budgets (rate limits, audit throttling): the IPv4 address, or
    /// the IPv6 /64 (one host usually owns a whole /64 and can rotate privacy addresses inside it at will).
    /// </summary>
    public static string SourceKey(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        address = Normalize(address)!;
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }
}

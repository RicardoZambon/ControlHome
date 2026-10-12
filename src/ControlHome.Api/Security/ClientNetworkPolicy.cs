using System.Net;

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
}

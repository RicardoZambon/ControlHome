using System.Net;
using System.Net.Sockets;

namespace ControlHome.Api.Security;

/// <summary>
/// Keeps denied-network reports (log line + audit row) from flooding: at most one per source per <see cref="Window"/>,
/// where a source is an IPv4 address or an IPv6 /64 (one host usually owns a whole /64). At most
/// <see cref="MaxTrackedSources"/> sources are tracked per window; while that many are tracked, new sources are not
/// reported, which also bounds the rows written per window.
/// </summary>
internal sealed class NetworkDeniedAuditThrottle(TimeProvider timeProvider)
{
    public const int MaxTrackedSources = 1024;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, DateTimeOffset> _lastReported = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>True when <paramref name="address"/>'s source should be reported now (and starts its window).</summary>
    public bool TryAcquire(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        var source = SourceKey(address);
        var now = timeProvider.GetUtcNow();
        lock (_lock)
        {
            if (_lastReported.TryGetValue(source, out var last))
            {
                if (now - last < Window)
                {
                    return false;
                }
            }
            else if (_lastReported.Count >= MaxTrackedSources)
            {
                RemoveExpired(now);
                if (_lastReported.Count >= MaxTrackedSources)
                {
                    return false;
                }
            }

            _lastReported[source] = now;
            return true;
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var (source, last) in _lastReported)
        {
            if (now - last >= Window)
            {
                _lastReported.Remove(source);
            }
        }
    }

    private static string SourceKey(IPAddress address)
    {
        address = ClientNetworkPolicy.Normalize(address)!;
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }
}

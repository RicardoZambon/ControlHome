namespace ControlHome.Application.Devices;

/// <summary>
/// Hardware identity of devices, to spot the same device reached through two integrations (e.g. a plug both in its vendor
/// cloud and over MQTT): connectors report identifiers as <c>Device.ConnectionsJson</c> (<c>mac</c>, <c>serial</c>, …).
/// </summary>
public static class DeviceIdentity
{
    public const string MacKey = "mac";
    public const string SerialKey = "serial";

    /// <summary>
    /// The comparable identifiers of a device: <c>mac:&lt;12 lower-case hex digits&gt;</c> (separators ignored; the all-zero
    /// and broadcast addresses are not identities) and <c>serial:&lt;upper-case serial&gt;</c> (at least 4 characters).
    /// </summary>
    public static IEnumerable<string> HardwareKeys(IReadOnlyDictionary<string, string> connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        if (connections.TryGetValue(MacKey, out var mac) && NormalizeMac(mac) is { } normalizedMac)
        {
            yield return $"{MacKey}:{normalizedMac}";
        }

        if (connections.TryGetValue(SerialKey, out var serial) && serial?.Trim() is { Length: >= 4 } trimmed)
        {
            yield return $"{SerialKey}:{trimmed.ToUpperInvariant()}";
        }
    }

    /// <summary>The MAC address as 12 lower-case hex digits, or <c>null</c> when it is not one (or carries no identity).</summary>
    public static string? NormalizeMac(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var digits = new string([.. value.Where(c => c is not (':' or '-' or '.' or ' '))]).ToLowerInvariant();
        return digits.Length == 12 && digits.All(Uri.IsHexDigit) && digits is not ("000000000000" or "ffffffffffff") ? digits : null;
    }
}

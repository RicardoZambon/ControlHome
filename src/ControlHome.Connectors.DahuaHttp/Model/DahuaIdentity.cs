using System.Text.Json.Serialization;

namespace ControlHome.Connectors.DahuaHttp.Model;

/// <summary>What an integration talks to. Stored in the config as the camelCase names below.</summary>
internal enum DahuaDeviceKind
{
    /// <summary>Setup only: decided from <c>getDeviceType</c> and the channel count.</summary>
    [JsonStringEnumMemberName("auto")]
    Auto,

    /// <summary>An IP camera (one video channel).</summary>
    [JsonStringEnumMemberName("camera")]
    Camera,

    /// <summary>An NVR/DVR: every video channel becomes a camera child device.</summary>
    [JsonStringEnumMemberName("recorder")]
    Recorder,

    /// <summary>An SS access-control terminal: a door opener (remote release), door contact and access events.</summary>
    [JsonStringEnumMemberName("accessControl")]
    AccessControl,

    /// <summary>An XPE 3200 IP door station: HTTP relay only, behind the <c>Connectors:DahuaHttp:XpeRelay</c> flag.</summary>
    [JsonStringEnumMemberName("xpe")]
    XpeIntercom,
}

/// <summary>The device's answers to <c>magicBox.cgi</c> (<c>getDeviceType</c>, <c>getSoftwareVersion</c>, …).</summary>
/// <param name="DeviceType">The model, e.g. <c>VIP 1230 B G4</c>, <c>NVD 1404</c>, <c>SS 3530 MF FACE</c>.</param>
/// <param name="SoftwareVersion">The firmware, e.g. <c>2.800.00IB000.0.R, Build Date 2023-03-01</c>.</param>
/// <param name="SerialNumber">The serial (<c>getSerialNo</c>), when the firmware answers it.</param>
/// <param name="MachineName">The device name set by the installer (<c>getMachineName</c>), when available.</param>
/// <param name="Vendor">The vendor (<c>getVendor</c>), when available.</param>
internal sealed record DahuaIdentity(string DeviceType, string? SoftwareVersion, string? SerialNumber, string? MachineName, string? Vendor)
{
    /// <summary>Upper-case model without spaces, dashes or underscores (<c>VIP1230BG4</c>), for pattern checks.</summary>
    public string NormalizedType => new([.. DeviceType.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit)]);
}

/// <summary>Picks a <see cref="DahuaDeviceKind"/> for <see cref="DahuaDeviceKind.Auto"/>.</summary>
internal static class DahuaKindDetector
{
    // Intelbras and Dahua model families.
    private static readonly string[] _recorderMarkers = ["NVR", "DVR", "XVR", "HCVR", "NVD", "MHDX"];
    private static readonly string[] _accessMarkers = ["ASI", "ASC", "ASA", "ACCESS"];

    /// <param name="channelCount">The number of video channels (<c>ChannelTitle</c> entries); 0 when unknown.</param>
    public static DahuaDeviceKind Detect(DahuaIdentity identity, int channelCount)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var type = identity.NormalizedType;

        // Intelbras access terminals are "SS 3530 MF FACE", "SS 5530 MF FACE W", … (SS + digits).
        if ((type.Length > 3 && type.StartsWith("SS", StringComparison.Ordinal) && char.IsAsciiDigit(type[2]))
            || _accessMarkers.Any(m => type.StartsWith(m, StringComparison.Ordinal) || type.Contains("DHI" + m, StringComparison.Ordinal)))
        {
            return DahuaDeviceKind.AccessControl;
        }

        if (_recorderMarkers.Any(m => type.Contains(m, StringComparison.Ordinal)) || channelCount > 1)
        {
            return DahuaDeviceKind.Recorder;
        }

        return DahuaDeviceKind.Camera;
    }
}

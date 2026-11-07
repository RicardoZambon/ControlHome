using System.Globalization;
using System.Net;
using System.Text.Json;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.DahuaHttp.Model;

namespace ControlHome.Connectors.DahuaHttp;

/// <summary>
/// A Dahua-family integration's parsed (non-secret) configuration: one physical device on the LAN. The password is a
/// secret field: it never appears here; the runtime reads it from the integration's secret store.
/// </summary>
/// <param name="Host">The device's host name or IP address (no scheme, no port).</param>
/// <param name="Port">The HTTP port of the CGI API (80 by default).</param>
/// <param name="Username">The device user (a dedicated low-privilege user is recommended).</param>
/// <param name="Kind">What the device is (<see cref="DahuaDeviceKind.Auto"/> only before setup decided it).</param>
/// <param name="Channels">The recorder channels to import, or <c>null</c> for all of them.</param>
/// <param name="RtspPort">The RTSP port of the live streams (554 by default).</param>
/// <param name="DoorUserId">The <c>UserID</c> sent with a remote door release (<c>101</c> by default).</param>
/// <param name="Language">The language of the names devices are first imported with (<c>pt-BR</c> or <c>en</c>).</param>
internal sealed record DahuaSettings(
    string Host,
    int Port,
    string Username,
    DahuaDeviceKind Kind,
    ChannelSelection? Channels,
    int RtspPort,
    string DoorUserId,
    string Language)
{
    public const string HostField = "host";
    public const string PortField = "port";
    public const string UsernameField = "username";
    public const string PasswordField = "password";
    public const string KindField = "kind";
    public const string ChannelsField = "channels";
    public const string RtspPortField = "rtspPort";
    public const string DoorUserIdField = "doorUserId";
    public const string LanguageField = "language";

    public const int DefaultPort = 80;
    public const int DefaultRtspPort = 554;
    public const string DefaultUsername = "admin";
    public const string DefaultDoorUserId = "101";

    /// <summary>The kinds as stored in the config.</summary>
    public static readonly IReadOnlyDictionary<string, DahuaDeviceKind> Kinds = new Dictionary<string, DahuaDeviceKind>(StringComparer.Ordinal)
    {
        ["auto"] = DahuaDeviceKind.Auto,
        ["camera"] = DahuaDeviceKind.Camera,
        ["recorder"] = DahuaDeviceKind.Recorder,
        ["accessControl"] = DahuaDeviceKind.AccessControl,
        ["xpe"] = DahuaDeviceKind.XpeIntercom,
    };

    /// <summary>"host:port", for status messages (never contains credentials).</summary>
    public string Endpoint => string.Create(CultureInfo.InvariantCulture, $"{HostForUri}:{Port}");

    /// <summary>The CGI API's base address (<c>http://host:port/</c>).</summary>
    public Uri BaseUri => new(string.Create(CultureInfo.InvariantCulture, $"http://{HostForUri}:{Port}/"));

    /// <summary>The host as it appears in a URL (IPv6 literals in brackets).</summary>
    public string HostForUri => IPAddress.TryParse(Host, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
        ? $"[{Host}]"
        : Host;

    public static string KindName(DahuaDeviceKind kind) => Kinds.First(k => k.Value == kind).Key;

    /// <summary>Parses and validates a configuration object (the password member, if present, is ignored).</summary>
    /// <param name="descriptor">The descriptor whose fields the config must match (the XPE kind exists only behind its flag).</param>
    /// <exception cref="InvalidDahuaSettingsException">The configuration does not match the descriptor's config fields.</exception>
    public static DahuaSettings Parse(JsonElement config, DahuaHttpConnectorDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var validation = FieldSchemaValidator.Validate(descriptor.ConfigFields, config);
        if (!validation.IsValid)
        {
            var problems = string.Join(", ", validation.Errors.Select(e => $"{e.Argument ?? "config"}: {e.Kind}"));
            throw new InvalidDahuaSettingsException(validation.Errors[0].Argument ?? HostField, $"Invalid configuration ({problems}).");
        }

        var host = Text(config, HostField)?.Trim().Trim('[', ']');
        if (string.IsNullOrEmpty(host) || host.Contains("://", StringComparison.Ordinal) || host.Any(c => char.IsWhiteSpace(c) || c is '/' or '@' or '?' or '#')
            || Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            throw new InvalidDahuaSettingsException(HostField, "Invalid configuration (host: a host name or IP address is required).");
        }

        var username = Text(config, UsernameField)?.Trim();
        if (string.IsNullOrEmpty(username) || username.Any(char.IsControl))
        {
            throw new InvalidDahuaSettingsException(UsernameField, "Invalid configuration (username is required).");
        }

        ChannelSelection? channels;
        try
        {
            channels = ChannelSelection.Parse(Text(config, ChannelsField));
        }
        catch (FormatException exception)
        {
            throw new InvalidDahuaSettingsException(ChannelsField, $"Invalid configuration (channels: {exception.Message})");
        }

        var doorUserId = Text(config, DoorUserIdField)?.Trim();
        if (string.IsNullOrEmpty(doorUserId))
        {
            doorUserId = DefaultDoorUserId;
        }
        else if (!doorUserId.All(char.IsAsciiLetterOrDigit))
        {
            throw new InvalidDahuaSettingsException(DoorUserIdField, "Invalid configuration (doorUserId: letters and digits only).");
        }

        return new DahuaSettings(
            host,
            Integer(config, PortField) ?? DefaultPort,
            username,
            Text(config, KindField) is { } kind && Kinds.TryGetValue(kind, out var parsed) ? parsed : DahuaDeviceKind.Auto,
            channels,
            Integer(config, RtspPortField) ?? DefaultRtspPort,
            doorUserId,
            string.Equals(Text(config, LanguageField), "pt-BR", StringComparison.OrdinalIgnoreCase) ? "pt-BR" : "en");
    }

    private static string? Text(JsonElement config, string field) =>
        config.ValueKind == JsonValueKind.Object && config.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Integer(JsonElement config, string field) =>
        config.ValueKind == JsonValueKind.Object && config.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
            ? number
            : null;
}

/// <summary>An invalid configuration; <see cref="Field"/> names the offending config field.</summary>
internal sealed class InvalidDahuaSettingsException(string field, string message) : ArgumentException(message, "config")
{
    public string Field { get; } = field;
}

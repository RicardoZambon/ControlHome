using System.Globalization;
using System.Text.Json;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.Mqtt.Transport;

namespace ControlHome.Connectors.Mqtt;

/// <summary>
/// An MQTT integration's parsed (non-secret) configuration. The password is a secret field: it never appears here; the
/// runtime reads it from the integration's secret store.
/// </summary>
/// <param name="Host">Broker host name or IP address.</param>
/// <param name="Port">Broker TCP port (1883, or usually 8883 with TLS).</param>
/// <param name="UseTls">Connect with TLS (the broker certificate must be trusted by the system).</param>
/// <param name="Username">User name, or <c>null</c> for an anonymous connection.</param>
/// <param name="ClientId">The MQTT client id, or <c>null</c> for <c>controlhome-&lt;integrationId&gt;</c>.</param>
/// <param name="DiscoveryPrefix">Home Assistant MQTT discovery prefix, or <c>null</c> when that intake is off.</param>
/// <param name="Zigbee2MqttBaseTopic">Zigbee2MQTT base topic, or <c>null</c> when that adapter is off.</param>
/// <param name="TasmotaDiscovery">Listen for Tasmota native discovery (<c>tasmota/discovery/#</c>).</param>
public sealed record MqttSettings(
    string Host,
    int Port,
    bool UseTls,
    string? Username,
    string? ClientId,
    string? DiscoveryPrefix,
    string? Zigbee2MqttBaseTopic,
    bool TasmotaDiscovery)
{
    public const string HostField = "host";
    public const string PortField = "port";
    public const string UseTlsField = "useTls";
    public const string UsernameField = "username";
    public const string PasswordField = "password";
    public const string ClientIdField = "clientId";
    public const string DiscoveryPrefixField = "discoveryPrefix";
    public const string Zigbee2MqttBaseTopicField = "zigbee2mqttBaseTopic";
    public const string TasmotaDiscoveryField = "tasmotaDiscovery";

    public const int DefaultPort = 1883;
    public const string DefaultDiscoveryPrefix = "homeassistant";
    public const string DefaultZigbee2MqttBaseTopic = "zigbee2mqtt";

    /// <summary>The topic prefix of Tasmota's native discovery (fixed by the firmware).</summary>
    public const string TasmotaDiscoveryPrefix = "tasmota/discovery";

    /// <summary>The client id for integration <paramref name="integrationId"/>: the configured one or <c>controlhome-&lt;id&gt;</c>.</summary>
    public string EffectiveClientId(Guid integrationId) =>
        ClientId ?? string.Create(CultureInfo.InvariantCulture, $"controlhome-{integrationId:D}");

    /// <summary>"host:port", for status messages (never contains credentials).</summary>
    public string Endpoint => string.Create(CultureInfo.InvariantCulture, $"{Host}:{Port}");

    /// <summary>Parses and validates a configuration object (the password member, if present, is ignored).</summary>
    /// <exception cref="ArgumentException">The configuration does not match the descriptor's config fields.</exception>
    public static MqttSettings Parse(JsonElement config)
    {
        var validation = FieldSchemaValidator.Validate(MqttConnectorDescriptor.Instance.ConfigFields, config);
        if (!validation.IsValid)
        {
            var problems = string.Join(", ", validation.Errors.Select(e => $"{e.Argument ?? "config"}: {e.Kind}"));
            throw new InvalidMqttSettingsException(validation.Errors[0].Argument ?? HostField, $"Invalid MQTT configuration ({problems}).");
        }

        var host = Text(config, HostField)?.Trim();
        if (string.IsNullOrEmpty(host) || host.Contains("://", StringComparison.Ordinal) || host.Any(char.IsWhiteSpace))
        {
            throw new InvalidMqttSettingsException(HostField, "Invalid MQTT configuration (host: a host name or IP address is required).");
        }

        return new MqttSettings(
            host,
            config.TryGetProperty(PortField, out var port) && port.ValueKind == JsonValueKind.Number ? port.GetInt32() : DefaultPort,
            Flag(config, UseTlsField) ?? false,
            Blank(Text(config, UsernameField)),
            Blank(Text(config, ClientIdField)?.Trim()),
            Topic(config, DiscoveryPrefixField, DefaultDiscoveryPrefix),
            Topic(config, Zigbee2MqttBaseTopicField, DefaultZigbee2MqttBaseTopic),
            Flag(config, TasmotaDiscoveryField) ?? true);
    }

    private static string? Text(JsonElement config, string field) =>
        config.ValueKind == JsonValueKind.Object && config.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? Flag(JsonElement config, string field) =>
        config.ValueKind == JsonValueKind.Object && config.TryGetProperty(field, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>A topic prefix: the default when absent, <c>null</c> (disabled) when blank, otherwise without wildcards.</summary>
    private static string? Topic(JsonElement config, string field, string defaultValue)
    {
        if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return defaultValue;
        }

        var topic = value.GetString()?.Trim().Trim('/');
        if (string.IsNullOrEmpty(topic))
        {
            return null;
        }

        if (!MqttTopic.IsValidName(topic) || topic.Contains("//", StringComparison.Ordinal))
        {
            throw new InvalidMqttSettingsException(field, $"Invalid MQTT configuration ({field}: wildcards and empty levels are not allowed).");
        }

        return topic;
    }
}

/// <summary>An invalid MQTT configuration; <see cref="Field"/> names the offending config field.</summary>
public sealed class InvalidMqttSettingsException(string field, string message) : ArgumentException(message, "config")
{
    public string Field { get; } = field;
}
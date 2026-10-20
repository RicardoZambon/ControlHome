using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.Mqtt.Transport;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Connectors.Mqtt;

/// <summary>
/// The MQTT connector (key <c>mqtt</c>): one broker connection per integration carrying three adapters — Zigbee2MQTT
/// (native), Home Assistant MQTT discovery (intake only) and Tasmota native discovery. Setup runs through
/// <see cref="CreateSetupFlow"/> (form → connection test → done); the same flow, started for an existing integration,
/// changes its connection settings or opens Zigbee2MQTT's permit-join window.
/// </summary>
public sealed class MqttConnectorFactory : IConnectorFactory
{
    public const string Key = "mqtt";

    private readonly MqttTransportFactory _transports;
    private readonly MqttRuntimeOptions _options;

    public MqttConnectorFactory()
        : this(null, null)
    {
    }

    /// <param name="transports">Creates broker connections (tests substitute a fake broker); MQTTnet by default.</param>
    /// <param name="options">Timing of the reconnect loop and connection tests.</param>
    internal MqttConnectorFactory(MqttTransportFactory? transports, MqttRuntimeOptions? options)
    {
        _transports = transports ?? MqttNetTransport.Create;
        _options = options ?? MqttRuntimeOptions.Default;
    }

    public IConnectorDescriptor Descriptor => MqttConnectorDescriptor.Instance;

    public IConnectorFlow? CreateSetupFlow() => new MqttSetupFlow(_transports, _options);

    /// <exception cref="ArgumentException">The config does not match <see cref="IConnectorDescriptor.ConfigFields"/>.</exception>
    public IConnectorRuntime CreateRuntime(IntegrationInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return new MqttRuntime(instance, MqttSettings.Parse(instance.Config), _transports, _options);
    }
}

/// <summary>The MQTT connector's descriptor.</summary>
public sealed class MqttConnectorDescriptor : IConnectorDescriptor
{
    public static MqttConnectorDescriptor Instance { get; } = new();

    public string Key => MqttConnectorFactory.Key;

    public LocalizedText DisplayName { get; } = new("MQTT (Zigbee2MQTT, Tasmota, Home Assistant discovery)", "MQTT (Zigbee2MQTT, Tasmota, descoberta do Home Assistant)");

    public string Icon => "radio-tower";

    public ConnectorCategory Category => ConnectorCategory.Protocol;

    public IReadOnlyList<FieldSchema> ConfigFields { get; } =
    [
        new FieldSchema(
            MqttSettings.HostField,
            new LocalizedText("Broker host", "Host do broker"),
            new StringSchema(255),
            Required: true,
            Secret: false,
            new LocalizedText(
                "Host name or IP address of the MQTT broker (on Umbrel: the Umbrel's address).",
                "Nome ou endereço IP do broker MQTT (no Umbrel: o endereço do Umbrel).")),
        new FieldSchema(
            MqttSettings.PortField,
            new LocalizedText("Port", "Porta"),
            new NumberSchema(1, 65535, 1, null, Integer: true),
            Required: false,
            Secret: false,
            new LocalizedText("Default 1883 (8883 is usual with TLS).", "Padrão 1883 (8883 é comum com TLS).")),
        new FieldSchema(
            MqttSettings.UseTlsField,
            new LocalizedText("Use TLS", "Usar TLS"),
            BoolSchema.Instance,
            Required: false,
            Secret: false,
            new LocalizedText("The broker's certificate must be trusted by this server.", "O certificado do broker precisa ser confiável para este servidor.")),
        new FieldSchema(
            MqttSettings.UsernameField,
            new LocalizedText("User name", "Usuário"),
            new StringSchema(255),
            Required: false,
            Secret: false,
            null),
        new FieldSchema(
            MqttSettings.PasswordField,
            new LocalizedText("Password", "Senha"),
            new StringSchema(255),
            Required: false,
            Secret: true,
            null),
        new FieldSchema(
            MqttSettings.ClientIdField,
            new LocalizedText("Client id", "Client id"),
            new StringSchema(128),
            Required: false,
            Secret: false,
            new LocalizedText(
                "Leave empty for controlhome-<integration id>. Must be unique on the broker.",
                "Deixe vazio para controlhome-<id da integração>. Precisa ser único no broker.")),
        new FieldSchema(
            MqttSettings.DiscoveryPrefixField,
            new LocalizedText("Home Assistant discovery prefix", "Prefixo de descoberta do Home Assistant"),
            new StringSchema(128),
            Required: false,
            Secret: false,
            new LocalizedText(
                "Default homeassistant. Leave empty to ignore Home Assistant MQTT discovery.",
                "Padrão homeassistant. Deixe vazio para ignorar a descoberta MQTT do Home Assistant.")),
        new FieldSchema(
            MqttSettings.Zigbee2MqttBaseTopicField,
            new LocalizedText("Zigbee2MQTT base topic", "Tópico base do Zigbee2MQTT"),
            new StringSchema(128),
            Required: false,
            Secret: false,
            new LocalizedText("Default zigbee2mqtt. Leave empty if you do not use Zigbee2MQTT.", "Padrão zigbee2mqtt. Deixe vazio se você não usa o Zigbee2MQTT.")),
        new FieldSchema(
            MqttSettings.TasmotaDiscoveryField,
            new LocalizedText("Tasmota discovery", "Descoberta Tasmota"),
            BoolSchema.Instance,
            Required: false,
            Secret: false,
            new LocalizedText("Find Tasmota devices (native discovery, tasmota/discovery).", "Encontrar dispositivos Tasmota (descoberta nativa, tasmota/discovery).")),
    ];

    public IReadOnlyList<FieldSchema> DeviceConfigFields { get; } = [];

    public ConnectorFeatures Features { get; } = new() { SetupFlow = true, PushInventory = true, PermitJoin = true };
}

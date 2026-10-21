using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.DahuaHttp.Protocol;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Connectors.DahuaHttp;

/// <summary>Settings bound from configuration section <see cref="SectionName"/> (e.g. <c>Connectors__DahuaHttp__XpeRelay=true</c>).</summary>
public sealed class DahuaHttpConnectorOptions
{
    public const string SectionName = "Connectors:DahuaHttp";

    /// <summary>
    /// Offers the XPE 3200 door station's HTTP relay (<c>/fcgi/do?action=OpenDoor</c>) as a device kind. Off by default:
    /// the URL is documented only second-hand and has not been tested on real hardware yet.
    /// </summary>
    public bool XpeRelay { get; set; }
}

/// <summary>
/// The Intelbras / Dahua HTTP connector (key <c>dahuahttp</c>): one integration per physical device on the LAN — an IP
/// camera, a recorder (each channel a camera child device) or an SS access terminal — over the CGI API with HTTP Digest
/// authentication. Setup runs through <see cref="DahuaSetupFlow"/> (form → identification → a checklist when the device
/// refuses → done); the same flow updates an existing integration (new password, address).
/// </summary>
public sealed class DahuaHttpConnectorFactory : IConnectorFactory
{
    public const string Key = "dahuahttp";

    private readonly DahuaHttpConnectorOptions _options;
    private readonly DahuaClientFactory _clients;
    private readonly DahuaRuntimeOptions _runtimeOptions;

    /// <param name="options">The connector's feature flags (DI: bound from <see cref="DahuaHttpConnectorOptions.SectionName"/>).</param>
    public DahuaHttpConnectorFactory(DahuaHttpConnectorOptions? options = null)
        : this(options, null, null)
    {
    }

    /// <param name="clients">Creates the device clients (tests point them at WireMock and shorten timeouts).</param>
    /// <param name="runtimeOptions">Timing of the runtime and the flow.</param>
    internal DahuaHttpConnectorFactory(DahuaHttpConnectorOptions? options, DahuaClientFactory? clients, DahuaRuntimeOptions? runtimeOptions)
    {
        _options = options ?? new DahuaHttpConnectorOptions();
        _runtimeOptions = runtimeOptions ?? DahuaRuntimeOptions.Default;
        _clients = clients ?? DahuaHttpClient.CreateFactory(_runtimeOptions);
        Descriptor = new DahuaHttpConnectorDescriptor(_options.XpeRelay);
    }

    IConnectorDescriptor IConnectorFactory.Descriptor => Descriptor;

    public DahuaHttpConnectorDescriptor Descriptor { get; }

    public IConnectorFlow? CreateSetupFlow() => new DahuaSetupFlow(Descriptor, _clients, _runtimeOptions);

    /// <exception cref="ArgumentException">The config does not match <see cref="IConnectorDescriptor.ConfigFields"/>.</exception>
    public IConnectorRuntime CreateRuntime(IntegrationInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return new DahuaRuntime(instance, DahuaSettings.Parse(instance.Config, Descriptor), _clients, _runtimeOptions);
    }
}

/// <summary>The connector's descriptor (the XPE kind is listed only behind its feature flag).</summary>
public sealed class DahuaHttpConnectorDescriptor : IConnectorDescriptor
{
    public DahuaHttpConnectorDescriptor(bool xpeRelay)
    {
        XpeRelay = xpeRelay;
        string[] kinds = xpeRelay ? ["auto", "camera", "recorder", "accessControl", "xpe"] : ["auto", "camera", "recorder", "accessControl"];
        ConfigFields =
        [
            new FieldSchema(
                DahuaSettings.HostField,
                new LocalizedText("Device address", "Endereço do dispositivo"),
                new StringSchema(255),
                Required: true,
                Secret: false,
                new LocalizedText(
                    "IP address or host name of the camera, recorder or access terminal on your network (give it a fixed IP in the router).",
                    "Endereço IP ou nome da câmera, do gravador ou do terminal de acesso na sua rede (reserve um IP fixo no roteador).")),
            new FieldSchema(
                DahuaSettings.PortField,
                new LocalizedText("HTTP port", "Porta HTTP"),
                new NumberSchema(1, 65535, 1, null, Integer: true),
                Required: false,
                Secret: false,
                new LocalizedText("Default 80.", "Padrão 80.")),
            new FieldSchema(
                DahuaSettings.UsernameField,
                new LocalizedText("User name", "Usuário"),
                new StringSchema(64),
                Required: true,
                Secret: false,
                new LocalizedText(
                    "A user of the device. A dedicated user with only the permissions ControlHome needs is safer than admin.",
                    "Um usuário do dispositivo. Um usuário só para o ControlHome, com as permissões necessárias, é mais seguro que o admin.")),
            new FieldSchema(
                DahuaSettings.PasswordField,
                new LocalizedText("Password", "Senha"),
                new StringSchema(128),
                Required: false,
                Secret: true,
                new LocalizedText(
                    "Several wrong passwords in a row lock the device for a while (often 30 minutes).",
                    "Várias senhas erradas seguidas bloqueiam o dispositivo por um tempo (muitas vezes 30 minutos).")),
            new FieldSchema(
                DahuaSettings.KindField,
                new LocalizedText("Device type", "Tipo de dispositivo"),
                new EnumSchema(kinds),
                Required: false,
                Secret: false,
                new LocalizedText(
                    xpeRelay
                        ? "auto: detected from the device. xpe: XPE 3200 door station relay (untested on hardware)."
                        : "auto: detected from the device (camera, recorder or access terminal).",
                    xpeRelay
                        ? "auto: detectado pelo dispositivo. xpe: relé do porteiro XPE 3200 (não testado em hardware)."
                        : "auto: detectado pelo dispositivo (câmera, gravador ou terminal de acesso).")),
            new FieldSchema(
                DahuaSettings.ChannelsField,
                new LocalizedText("Recorder channels", "Canais do gravador"),
                new StringSchema(256),
                Required: false,
                Secret: false,
                new LocalizedText("Only these channels, e.g. 1-4, 6. Empty: every channel.", "Só estes canais, por exemplo 1-4, 6. Vazio: todos os canais."))
            {
                Advanced = true,
            },
            new FieldSchema(
                DahuaSettings.RtspPortField,
                new LocalizedText("RTSP port", "Porta RTSP"),
                new NumberSchema(1, 65535, 1, null, Integer: true),
                Required: false,
                Secret: false,
                new LocalizedText("For live video. Default 554.", "Para o vídeo ao vivo. Padrão 554."))
            {
                Advanced = true,
            },
            new FieldSchema(
                DahuaSettings.DoorUserIdField,
                new LocalizedText("User ID for remote door release", "ID de usuário da abertura remota"),
                new StringSchema(32),
                Required: false,
                Secret: false,
                new LocalizedText("Sent with the door release (UserID). Default 101.", "Enviado com a abertura da porta (UserID). Padrão 101."))
            {
                Advanced = true,
            },
            new FieldSchema(
                DahuaSettings.LanguageField,
                new LocalizedText("Language of device names", "Idioma dos nomes dos dispositivos"),
                new EnumSchema(["en", "pt-BR"]),
                Required: false,
                Secret: false,
                null)
            {
                Advanced = true,
            },
        ];
    }

    /// <summary>Whether the XPE door-station relay is offered (<see cref="DahuaHttpConnectorOptions.XpeRelay"/>).</summary>
    public bool XpeRelay { get; }

    public string Key => DahuaHttpConnectorFactory.Key;

    public LocalizedText DisplayName { get; } = new(
        "Intelbras cameras, recorders and access control",
        "Câmeras, gravadores e controle de acesso Intelbras");

    public string Icon => "cctv";

    public ConnectorCategory Category => ConnectorCategory.Vendor;

    public IReadOnlyList<FieldSchema> ConfigFields { get; }

    public IReadOnlyList<FieldSchema> DeviceConfigFields { get; } = [];

    public ConnectorFeatures Features { get; } = new() { SetupFlow = true, Reauth = true, PushInventory = true };
}

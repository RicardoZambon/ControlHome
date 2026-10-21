using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.SmartThings.Flow;
using ControlHome.Connectors.SmartThings.Runtime;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Connectors.SmartThings;

/// <summary>
/// The SmartThings connector (key <c>smartthings</c>): one integration per SmartThings location, signed in with the
/// household's own OAuth-In app (or, for development, a 24-hour personal access token), polling the cloud API
/// adaptively. Setup and re-authorization run through <see cref="CreateSetupFlow"/>; devices are kept on re-authorization.
/// </summary>
public sealed class SmartThingsConnectorFactory : IConnectorFactory
{
    public const string Key = "smartthings";

    private readonly Func<HttpClient> _http;
    private readonly IQuotaCounter? _quotas;
    private readonly SmartThingsOptions _options;
    private readonly CapabilityDefinitionCache _definitions;

    /// <param name="httpClientFactory">Creates the named client registered by <see cref="SmartThingsServiceCollectionExtensions.AddSmartThingsHttpClient"/>.</param>
    /// <param name="quotaCounter">Persisted quota counters (the rooms API's hourly budget); in memory when the host has none.</param>
    public SmartThingsConnectorFactory(IHttpClientFactory httpClientFactory, IQuotaCounter? quotaCounter = null)
        : this(() => httpClientFactory.CreateClient(SmartThingsServiceCollectionExtensions.HttpClientName), quotaCounter, null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
    }

    /// <param name="http">Creates HTTP clients (tests: a handler pointed at WireMock).</param>
    internal SmartThingsConnectorFactory(Func<HttpClient> http, IQuotaCounter? quotaCounter, SmartThingsOptions? options)
    {
        _http = http;
        _quotas = quotaCounter;
        _options = options ?? SmartThingsOptions.Default;
        _definitions = new CapabilityDefinitionCache(_options);
    }

    public IConnectorDescriptor Descriptor => SmartThingsConnectorDescriptor.Instance;

    public IConnectorFlow? CreateSetupFlow() => new SmartThingsSetupFlow(_http, _options, SetupInstructions.Default);

    /// <exception cref="ArgumentException">The config does not match <see cref="IConnectorDescriptor.ConfigFields"/>.</exception>
    public IConnectorRuntime CreateRuntime(IntegrationInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return new SmartThingsRuntime(instance, SmartThingsSettings.Parse(instance.Config, _options), _http, _quotas, _options, _definitions);
    }
}

/// <summary>The SmartThings connector's descriptor.</summary>
public sealed class SmartThingsConnectorDescriptor : IConnectorDescriptor
{
    public static SmartThingsConnectorDescriptor Instance { get; } = new();

    public string Key => SmartThingsConnectorFactory.Key;

    public LocalizedText DisplayName { get; } = new("Samsung SmartThings", "Samsung SmartThings");

    public string Icon => "cloud";

    public ConnectorCategory Category => ConnectorCategory.Vendor;

    public IReadOnlyList<FieldSchema> ConfigFields { get; } =
    [
        new FieldSchema(
            SmartThingsSettings.ModeField,
            new LocalizedText("Sign-in", "Acesso"),
            new EnumSchema([SmartThingsSettings.OAuthMode, SmartThingsSettings.PatMode]),
            Required: true,
            Secret: false,
            new LocalizedText(
                "oauth: your own SmartThings app (recommended). pat: a personal access token, development only (expires after 24 h).",
                "oauth: o seu próprio app SmartThings (recomendado). pat: um token de acesso pessoal, só para desenvolvimento (expira em 24 h).")),
        new FieldSchema(
            SmartThingsSettings.ClientIdField,
            new LocalizedText("OAuth client id", "Client id do OAuth"),
            new StringSchema(128),
            Required: false,
            Secret: false,
            new LocalizedText("Printed by the SmartThings CLI when you created your app.", "Mostrado pela CLI da SmartThings quando você criou o seu app.")),
        new FieldSchema(
            SmartThingsSettings.ClientSecretField,
            new LocalizedText("OAuth client secret", "Client secret do OAuth"),
            new StringSchema(256),
            Required: false,
            Secret: true,
            new LocalizedText("Printed once by the SmartThings CLI. Stored encrypted, never shown again.", "Mostrado uma única vez pela CLI da SmartThings. Guardado criptografado, nunca mais exibido.")),
        new FieldSchema(
            SmartThingsSettings.RedirectUriField,
            new LocalizedText("Redirect URL", "URL de redirecionamento"),
            new StringSchema(2048),
            Required: false,
            Secret: false,
            new LocalizedText("Exactly the redirect URI registered in your app.", "Exatamente a redirect URI registrada no seu app.")),
        new FieldSchema(
            SmartThingsSettings.EndpointsField,
            new LocalizedText("OAuth URLs", "URLs do OAuth"),
            new EnumSchema([SmartThingsSettings.DocumentedPreset, SmartThingsSettings.HomeAssistantPreset, SmartThingsSettings.CustomPreset]),
            Required: false,
            Secret: false,
            null),
        new FieldSchema(
            SmartThingsSettings.AuthorizeUrlField,
            new LocalizedText("Authorize URL", "URL de autorização"),
            new StringSchema(2048),
            Required: false,
            Secret: false,
            null) { Advanced = true },
        new FieldSchema(
            SmartThingsSettings.TokenUrlField,
            new LocalizedText("Token URL", "URL do token"),
            new StringSchema(2048),
            Required: false,
            Secret: false,
            null) { Advanced = true },
        new FieldSchema(
            SmartThingsSettings.PersonalAccessTokenField,
            new LocalizedText("Personal access token", "Token de acesso pessoal"),
            new StringSchema(256),
            Required: false,
            Secret: true,
            new LocalizedText("Development only: tokens created since 30 Dec 2024 expire after 24 hours.", "Só para desenvolvimento: tokens criados desde 30/12/2024 expiram em 24 horas.")),
        new FieldSchema(
            SmartThingsSettings.LocationIdField,
            new LocalizedText("Location", "Local"),
            new StringSchema(64),
            Required: true,
            Secret: false,
            null),
        new FieldSchema(
            SmartThingsSettings.LocationNameField,
            new LocalizedText("Location name", "Nome do local"),
            new StringSchema(128),
            Required: false,
            Secret: false,
            null),
        new FieldSchema(
            SmartThingsSettings.PollIntervalField,
            new LocalizedText("Polling interval", "Intervalo de consulta"),
            new NumberSchema(SmartThingsSettings.MinPollSeconds, SmartThingsSettings.MaxPollSeconds, 1, "s", Integer: true),
            Required: false,
            Secret: false,
            new LocalizedText(
                "How often all devices are read (30–120 s; slower when SmartThings' rate limits run low).",
                "Com que frequência todos os dispositivos são lidos (30–120 s; mais devagar quando os limites da SmartThings ficam baixos).")) { Advanced = true },
        new FieldSchema(
            SmartThingsSettings.PatIssuedAtField,
            new LocalizedText("Token entered at", "Token informado em"),
            new StringSchema(40),
            Required: false,
            Secret: false,
            null) { Advanced = true },
    ];

    public IReadOnlyList<FieldSchema> DeviceConfigFields { get; } = [];

    public ConnectorFeatures Features { get; } = new()
    {
        SetupFlow = true,
        Reauth = true,
        PushInventory = true,
        Cloud = true,
        Polling = true,
    };
}

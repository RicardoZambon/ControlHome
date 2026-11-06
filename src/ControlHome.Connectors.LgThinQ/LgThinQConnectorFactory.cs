using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.LgThinQ.Api;
using ControlHome.Connectors.LgThinQ.Mqtt;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Connectors.LgThinQ;

/// <summary>
/// The LG ThinQ connector (key <c>lgthinq</c>): LG appliances through the ThinQ Connect personal access token API — REST
/// for inventory, state and control, outbound MQTT (AWS IoT, client certificate) for real-time state and notifications,
/// and nightly energy history. Setup runs through <see cref="CreateSetupFlow"/> (form → token check with
/// <c>GET devices</c> → done); the same flow, started for an existing integration, replaces the token in place.
/// </summary>
public sealed class LgThinQConnectorFactory : IConnectorFactory, IDisposable
{
    public const string Key = "lgthinq";

    private readonly LgRuntimeOptions _options;
    private readonly LgHttp _http;
    private readonly LgRateBudgets _budgets;
    private readonly LgMqttTransportFactory _transports;
    private readonly ConcurrentDictionary<Guid, LgIntegrationMemory> _memory = new();

    public LgThinQConnectorFactory()
        : this(null, null, null)
    {
    }

    /// <param name="handler">Sends the REST requests (tests: a handler or WireMock via <see cref="LgRuntimeOptions.BaseUrlOverride"/>).</param>
    /// <param name="options">Timing and limits.</param>
    /// <param name="transports">Creates MQTT connections; MQTTnet by default.</param>
    internal LgThinQConnectorFactory(HttpMessageHandler? handler, LgRuntimeOptions? options, LgMqttTransportFactory? transports)
    {
        _options = options ?? LgRuntimeOptions.Default;
        _http = new LgHttp(handler, _options.Http);
        _budgets = new LgRateBudgets(_options.Rate);
        _transports = transports ?? LgMqttNetTransport.Create;
    }

    public IConnectorDescriptor Descriptor => LgThinQConnectorDescriptor.Instance;

    internal LgRuntimeOptions Options => _options;

    internal LgRateBudgets Budgets => _budgets;

    /// <summary>The roots LG's broker certificate must chain to: the bundled Amazon Root CA 1 (or a test CA).</summary>
    internal X509Certificate2Collection TrustedRoots => _options.TrustedRootsOverride ?? [LgTrust.AmazonRootCa1];

    public IConnectorFlow? CreateSetupFlow() => new LgThinQSetupFlow(this);

    /// <exception cref="ArgumentException">The config does not match <see cref="IConnectorDescriptor.ConfigFields"/>.</exception>
    public IConnectorRuntime CreateRuntime(IntegrationInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var settings = LgThinQSettings.Parse(instance.Config, TimeZoneInfo.Local);
        return new LgThinQRuntime(instance, settings, this, _memory.GetOrAdd(instance.Id, _ => new LgIntegrationMemory()), _transports);
    }

    public void Dispose()
    {
        _budgets.Dispose();
        _http.Dispose();
    }

    /// <summary>An API client for one token (shared budget per token; headers per integration).</summary>
    internal LgApiClient CreateApi(LgThinQSettings settings, string pat, string clientId, TimeProvider time, Microsoft.Extensions.Logging.ILogger logger)
    {
        var budget = _budgets.For(pat, time);
        var http = _http.CreateClient(_options.BaseUrlOverride ?? settings.BaseUrl, new LgIdentity(pat, settings.Country, clientId, settings.ApiKey), budget, time);
        return new LgApiClient(http, budget, logger, time, _options.Http.AttemptTimeout);
    }
}

/// <summary>The LG ThinQ connector's descriptor.</summary>
public sealed class LgThinQConnectorDescriptor : IConnectorDescriptor
{
    public static LgThinQConnectorDescriptor Instance { get; } = new();

    public string Key => LgThinQConnectorFactory.Key;

    public LocalizedText DisplayName { get; } = new("LG ThinQ", "LG ThinQ");

    public string Icon => "washing-machine";

    public ConnectorCategory Category => ConnectorCategory.Vendor;

    public IReadOnlyList<FieldSchema> ConfigFields { get; } =
    [
        new FieldSchema(
            LgThinQSettings.PatField,
            new LocalizedText("Personal access token", "Token de acesso pessoal"),
            new StringSchema(4096),
            Required: true,
            Secret: true,
            new LocalizedText(
                "Created at connect-pat.lgthinq.com with your LG ThinQ account. Stored encrypted, never shown again.",
                "Criado em connect-pat.lgthinq.com com a sua conta LG ThinQ. Guardado criptografado, nunca mais exibido.")),
        new FieldSchema(
            LgThinQSettings.CountryField,
            new LocalizedText("Country of the LG account", "País da conta LG"),
            new EnumSchema(LgCountries.All),
            Required: false,
            Secret: false,
            new LocalizedText(
                "The country the LG account was created in (default BR, Brazil), not where you are now.",
                "O país em que a conta LG foi criada (padrão BR, Brasil), não onde você está agora.")),
        new FieldSchema(
            LgThinQSettings.EnergyField,
            new LocalizedText("Energy history", "Histórico de energia"),
            BoolSchema.Instance,
            Required: false,
            Secret: false,
            new LocalizedText(
                "Fetch each appliance's daily energy (Wh) once a night. LG has no live power readings.",
                "Buscar a energia diária (Wh) de cada aparelho uma vez por noite. A LG não informa a potência ao vivo."))
        {
            Advanced = true,
        },
        new FieldSchema(
            LgThinQSettings.TimeZoneField,
            new LocalizedText("Time zone of the energy days", "Fuso horário dos dias de energia"),
            new StringSchema(64),
            Required: false,
            Secret: false,
            new LocalizedText(
                "IANA time zone (e.g. America/Sao_Paulo). Leave empty for the country's main time zone.",
                "Fuso horário IANA (ex.: America/Sao_Paulo). Deixe vazio para o fuso principal do país."))
        {
            Advanced = true,
        },
        new FieldSchema(
            LgThinQSettings.ApiKeyField,
            new LocalizedText("API key (x-api-key)", "Chave da API (x-api-key)"),
            new StringSchema(256),
            Required: false,
            Secret: false,
            new LocalizedText(
                "Leave empty for the public key of LG's official SDK. Change it only if LG publishes a new one.",
                "Deixe vazio para a chave pública do SDK oficial da LG. Altere apenas se a LG publicar outra."))
        {
            Advanced = true,
        },
    ];

    public IReadOnlyList<FieldSchema> DeviceConfigFields { get; } = [];

    public ConnectorFeatures Features { get; } = new() { SetupFlow = true, Reauth = true, PushInventory = true, Cloud = true };
}

using System.Text.Json;
using System.Text.Json.Nodes;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.LgThinQ.Api;
using ControlHome.Connectors.LgThinQ.Model;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Connectors.LgThinQ;

/// <summary>
/// The LG ThinQ connector's flow: one form (name, personal access token, country; energy, time zone and API key under
/// "Advanced") → the token is checked with <c>GET devices</c> (wrong token, terms not accepted, wrong country and the call
/// limit come back as field errors) → done, with the token and the integration's persistent client id
/// (<c>controlhome-&lt;guid&gt;</c>) as secrets. Started for an existing integration it is the "replace token" flow: the
/// form is pre-filled (a blank token keeps the stored one), and the client id and the MQTT certificate are kept, so devices,
/// rooms and automations stay as they are.
/// </summary>
internal sealed class LgThinQSetupFlow(LgThinQConnectorFactory factory) : IConnectorFlow
{
    public const string AccountStep = "account";
    public const string NameField = "name";

    /// <summary>The scopes the token needs (as listed on connect-pat.lgthinq.com).</summary>
    public static readonly LocalizedText Scopes = new(
        "view all devices, view all device statuses, device control, event subscription, push notification, energy consumption inquiry",
        "visualizar todos os dispositivos, visualizar o status de todos os dispositivos, controle de dispositivos, assinatura de eventos, notificações push, consulta de consumo de energia");

    private FlowContext? _context;

    private static IReadOnlyList<FieldSchema> Fields { get; } =
    [
        new FieldSchema(
            NameField,
            new LocalizedText("Name", "Nome"),
            new StringSchema(128),
            Required: true,
            Secret: false,
            null),
        .. LgThinQConnectorDescriptor.Instance.ConfigFields,
    ];

    public Task<FlowStep> StartAsync(FlowContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        var values = context.Existing is { } existing
            ? WithName(LgThinQSettings.WithoutSecrets(existing.Config), existing.Name)
            : JsonSerializer.SerializeToElement(new JsonObject
            {
                [NameField] = "LG ThinQ",
                [LgThinQSettings.CountryField] = LgCountries.Default,
                [LgThinQSettings.EnergyField] = true,
            });
        return Task.FromResult<FlowStep>(Form(values, new Dictionary<string, LocalizedText>(), context.Existing is not null));
    }

    public async Task<FlowStep> ContinueAsync(string stepId, JsonElement input, CancellationToken cancellationToken)
    {
        var context = _context ?? throw new InvalidOperationException("The flow has not started.");
        if (stepId != AccountStep)
        {
            return new AbortStep("unknown_step", new LocalizedText("This step is not part of the flow.", "Esta etapa não faz parte do fluxo."));
        }

        var replacing = context.Existing is not null;
        var values = WithoutPat(input);
        var errors = new Dictionary<string, LocalizedText>(StringComparer.Ordinal);

        // A blank token keeps the stored one (replace-token flow); a new integration needs one.
        var given = input.ValueKind == JsonValueKind.Object && input.TryGetProperty(LgThinQSettings.PatField, out var patValue)
            && patValue.ValueKind == JsonValueKind.String ? patValue.GetString()?.Trim() : null;
        var pat = string.IsNullOrEmpty(given) && context.Existing is { } current
            ? await current.Secrets.GetAsync(LgSecrets.Pat, cancellationToken).ConfigureAwait(false)
            : given;
        var check = WithPat(values, pat);
        var validation = FieldSchemaValidator.Validate(Fields, check);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
            {
                errors.TryAdd(error.Argument ?? LgThinQSettings.PatField, FieldError(error.Kind));
            }

            return Form(values, errors, replacing);
        }

        LgThinQSettings settings;
        try
        {
            settings = LgThinQSettings.Parse(values, TimeZoneInfo.Local);
        }
        catch (InvalidLgThinQSettingsException exception)
        {
            errors[exception.Field] = new LocalizedText("This value is not valid.", "Este valor não é válido.");
            return Form(values, errors, replacing);
        }

        var clientId = context.Existing is { } kept
            ? await kept.Secrets.GetAsync(LgSecrets.ClientId, cancellationToken).ConfigureAwait(false)
            : null;
        clientId ??= NewClientId();

        var api = factory.CreateApi(settings, pat!, clientId, context.Time, context.Logger);
        int devices;
        try
        {
            devices = LgDeviceInfo.ParseList(await api.GetDevicesAsync(cancellationToken).ConfigureAwait(false)).Count;
        }
        catch (LgApiException exception)
        {
            var (field, message) = Problem(exception);
            errors[field] = message;
            return Form(values, errors, replacing);
        }
        catch (LgQuotaBlockedException exception)
        {
            errors[LgThinQSettings.PatField] = QuotaMessage(exception.Until);
            return Form(values, errors, replacing);
        }

        var name = values.TryGetProperty(NameField, out var nameValue) && nameValue.GetString() is { Length: > 0 } text ? text.Trim() : "LG ThinQ";
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LgSecrets.Pat] = pat!,
            [LgSecrets.ClientId] = clientId,
        };

        // The flow's secrets replace the integration's: keep its MQTT certificate (reissuing costs LG calls and is throttled).
        if (context.Existing is { } existing
            && await existing.Secrets.GetAsync(LgSecrets.MqttCredentials, cancellationToken).ConfigureAwait(false) is { Length: > 0 } credentials)
        {
            secrets[LgSecrets.MqttCredentials] = credentials;
        }

        context.Logger.LogLgSetupChecked(devices);
        return new DoneStep(name, LgThinQSettings.WithoutSecrets(values)) { Secrets = secrets };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>A new persistent client id (random, never derived from anything identifying).</summary>
    public static string NewClientId() => $"controlhome-{Guid.NewGuid():D}";

    private static FormStep Form(JsonElement values, IReadOnlyDictionary<string, LocalizedText> errors, bool replacing) =>
        new(
            AccountStep,
            replacing
                ? new LocalizedText("Replace the LG ThinQ token", "Substituir o token LG ThinQ")
                : new LocalizedText("Connect your LG ThinQ account", "Conectar sua conta LG ThinQ"),
            replacing
                ? new LocalizedText(
                    "Paste a new personal access token (create it at https://connect-pat.lgthinq.com). Leave it empty to keep the current one. Your devices, rooms and automations are kept.",
                    "Cole um novo token de acesso pessoal (crie em https://connect-pat.lgthinq.com). Deixe vazio para manter o atual. Seus dispositivos, cômodos e automações são mantidos.")
                : new LocalizedText(
                    $"1. Open https://connect-pat.lgthinq.com and sign in with your LG ThinQ account. 2. Create a token with these scopes: {Scopes.En}. 3. Paste it below and pick the country the LG account was created in. ControlHome checks the token before saving.",
                    $"1. Abra https://connect-pat.lgthinq.com e entre com a sua conta LG ThinQ. 2. Crie um token com estas permissões: {Scopes.PtBr}. 3. Cole o token abaixo e escolha o país em que a conta LG foi criada. O ControlHome verifica o token antes de salvar."),
            Fields)
        {
            Values = values,
            Errors = errors,
        };

    private static (string Field, LocalizedText Message) Problem(LgApiException exception) => exception.Kind switch
    {
        LgErrorKind.NeedsReauth => (LgThinQSettings.PatField, new LocalizedText(
            "LG ThinQ rejected this token. Create a new one at connect-pat.lgthinq.com.",
            "A LG ThinQ recusou este token. Crie um novo em connect-pat.lgthinq.com.")),
        LgErrorKind.QuotaExceeded => (LgThinQSettings.PatField, new LocalizedText(
            "The LG ThinQ call limit of this account is used up. Try again in an hour.",
            "O limite de chamadas da LG ThinQ desta conta acabou. Tente de novo em uma hora.")),
        LgErrorKind.Account when exception.Code == LgErrors.InvalidServiceKey => (LgThinQSettings.ApiKeyField, new LocalizedText(
            "LG ThinQ rejected the API key (x-api-key). Leave it empty for the key of LG's official SDK, or enter the one LG published.",
            "A LG ThinQ recusou a chave da API (x-api-key). Deixe vazio para usar a chave do SDK oficial da LG, ou informe a que a LG publicou.")),
        LgErrorKind.Account when exception.Code == LgErrors.NotAcceptableTerms => (LgThinQSettings.PatField, new LocalizedText(
            "The LG account has not accepted LG ThinQ's terms. Open the LG ThinQ app, accept them and try again.",
            "A conta LG não aceitou os termos da LG ThinQ. Abra o app LG ThinQ, aceite os termos e tente de novo.")),
        LgErrorKind.Account => (LgThinQSettings.CountryField, new LocalizedText(
            "LG ThinQ does not know this account in this country. Pick the country the LG account was created in.",
            "A LG ThinQ não reconhece esta conta neste país. Escolha o país em que a conta LG foi criada.")),
        LgErrorKind.Transient => (LgThinQSettings.PatField, new LocalizedText(
            "LG ThinQ could not be reached. Check this server's internet connection and try again.",
            "Não foi possível alcançar a LG ThinQ. Verifique a conexão deste servidor com a internet e tente de novo.")),
        _ => (LgThinQSettings.PatField, new LocalizedText(
            $"LG ThinQ refused the request ({LgErrors.NameOf(exception.Code)}).",
            $"A LG ThinQ recusou a solicitação ({LgErrors.NameOf(exception.Code)}).")),
    };

    private static LocalizedText QuotaMessage(DateTimeOffset until) => new(
        $"LG ThinQ calls for this token are paused until {until:HH:mm} UTC (call limit reached).",
        $"As chamadas à LG ThinQ deste token estão pausadas até {until:HH:mm} UTC (limite de chamadas atingido).");

    private static LocalizedText FieldError(ValueErrorKind kind) => kind switch
    {
        ValueErrorKind.Required => new LocalizedText("Required.", "Obrigatório."),
        ValueErrorKind.Unknown => new LocalizedText("Unknown field.", "Campo desconhecido."),
        _ => new LocalizedText("This value is not valid.", "Este valor não é válido."),
    };

    private static JsonElement WithoutPat(JsonElement input)
    {
        var copy = input.ValueKind == JsonValueKind.Object ? JsonNode.Parse(input.GetRawText()) as JsonObject ?? [] : [];
        copy.Remove(LgThinQSettings.PatField);
        return JsonSerializer.SerializeToElement(copy);
    }

    private static JsonElement WithPat(JsonElement values, string? pat)
    {
        var copy = JsonNode.Parse(values.GetRawText()) as JsonObject ?? [];
        if (!string.IsNullOrEmpty(pat))
        {
            copy[LgThinQSettings.PatField] = pat;
        }

        return JsonSerializer.SerializeToElement(copy);
    }

    private static JsonElement WithName(JsonElement config, string name)
    {
        var copy = JsonNode.Parse(config.GetRawText()) as JsonObject ?? [];
        copy[NameField] = name;
        return JsonSerializer.SerializeToElement(copy);
    }
}

internal static partial class LgSetupLog
{
    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "LG ThinQ setup: the token works ({Devices} devices)")]
    public static partial void LogLgSetupChecked(this Microsoft.Extensions.Logging.ILogger logger, int devices);
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.Mqtt.Model;
using ControlHome.Connectors.Mqtt.Transport;
using ControlHome.Connectors.Mqtt.Zigbee2Mqtt;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Connectors.Mqtt;

/// <summary>
/// The MQTT connector's flow. Setup: a form with the connection settings → a connection test with a throw-away client id
/// (the form comes back with field errors when the broker cannot be reached or rejects the credentials) → done (the
/// password goes to the secrets, never the config). For an existing integration the flow first asks what to do: change the
/// connection settings (a blank password keeps the stored one while host, port, TLS and user name stay the same — it is
/// never sent to another broker) or let Zigbee devices join — it opens Zigbee2MQTT's
/// permit-join window (<c>bridge/request/permit_join</c> with <c>time</c>) and shows a progress step listing the devices
/// that joined until the window closes.
/// </summary>
internal sealed class MqttSetupFlow(MqttTransportFactory transports, MqttRuntimeOptions options) : IConnectorFlow
{
    public const string ConnectionStep = "connection";
    public const string ActionStep = "action";
    public const string PermitJoinStep = "permitJoin";
    public const string PermitJoinProgressStep = "permitJoinProgress";
    public const string SettingsChoice = "settings";
    public const string PermitJoinChoice = "permitJoin";
    public const string SecondsField = "seconds";
    public const string StopField = "stop";
    public const int DefaultPermitJoinSeconds = 254;

    private readonly Lock _lock = new();
    private readonly List<string> _joined = [];
    private FlowContext? _context;
    private IMqttTransport? _joinConnection;
    private string? _joinBaseTopic;
    private DateTimeOffset _joinUntil;

    private static IReadOnlyList<FieldSchema> PermitJoinFields { get; } =
    [
        new FieldSchema(
            SecondsField,
            new LocalizedText("Seconds", "Segundos"),
            new NumberSchema(1, 254, 1, "s", Integer: true),
            Required: false,
            Secret: false,
            new LocalizedText("How long new devices may join (at most 254 s).", "Por quanto tempo novos dispositivos podem parear (no máximo 254 s).")),
    ];

    public Task<FlowStep> StartAsync(FlowContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        if (context.Existing is not { } existing)
        {
            return Task.FromResult<FlowStep>(ConnectionForm(Defaults(), new Dictionary<string, LocalizedText>()));
        }

        var choices = new List<FlowChoice>
        {
            new(SettingsChoice, new LocalizedText("Change the connection settings", "Alterar as configurações de conexão"), null),
        };
        if (TryParse(existing.Config) is { Zigbee2MqttBaseTopic: not null })
        {
            choices.Add(new FlowChoice(
                PermitJoinChoice,
                new LocalizedText("Pair Zigbee devices", "Parear dispositivos Zigbee"),
                new LocalizedText("Lets new devices join the Zigbee2MQTT network for a while.", "Permite que novos dispositivos entrem na rede do Zigbee2MQTT por um tempo.")));
        }

        return Task.FromResult<FlowStep>(new ChooseStep(
            ActionStep,
            new LocalizedText("MQTT", "MQTT"),
            new LocalizedText("What do you want to do?", "O que você quer fazer?"),
            choices));
    }

    public async Task<FlowStep> ContinueAsync(string stepId, JsonElement input, CancellationToken cancellationToken)
    {
        var context = _context ?? throw new InvalidOperationException("The flow has not started.");
        switch (stepId)
        {
            case ConnectionStep:
                return await TestAndFinishAsync(context, input, cancellationToken).ConfigureAwait(false);
            case ActionStep when context.Existing is { } existing:
                return input.Str("choice") switch
                {
                    SettingsChoice => ConnectionForm(WithoutPassword(existing.Config), new Dictionary<string, LocalizedText>()),
                    PermitJoinChoice => new FormStep(
                        PermitJoinStep,
                        new LocalizedText("Pair Zigbee devices", "Parear dispositivos Zigbee"),
                        new LocalizedText(
                            "Zigbee2MQTT will accept new devices for this long. Put each device in pairing mode while it waits.",
                            "O Zigbee2MQTT aceitará novos dispositivos por este tempo. Coloque cada dispositivo em modo de pareamento enquanto isso."),
                        PermitJoinFields) { Values = JsonSerializer.SerializeToElement(new { seconds = DefaultPermitJoinSeconds }) },
                    _ => new AbortStep("unknown_choice", new LocalizedText("Unknown choice.", "Opção desconhecida.")),
                };
            case PermitJoinStep when context.Existing is { } existing:
                return await StartPermitJoinAsync(context, existing, input, cancellationToken).ConfigureAwait(false);
            case PermitJoinProgressStep when context.Existing is { } existing:
                return await PermitJoinProgressAsync(context, existing, input, cancellationToken).ConfigureAwait(false);
            default:
                return new AbortStep("unknown_step", new LocalizedText("This step is not part of the flow.", "Esta etapa não faz parte do fluxo."));
        }
    }

    public async ValueTask DisposeAsync() => await CloseJoinConnectionAsync().ConfigureAwait(false);

    // ---- setup / settings -------------------------------------------------------------------------------------------------

    private async Task<FlowStep> TestAndFinishAsync(FlowContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, LocalizedText>(StringComparer.Ordinal);
        var values = WithoutPassword(input);
        var validation = FieldSchemaValidator.Validate(MqttConnectorDescriptor.Instance.ConfigFields, input);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
            {
                errors.TryAdd(error.Argument ?? MqttSettings.HostField, FieldError(error.Kind));
            }

            return ConnectionForm(values, errors);
        }

        MqttSettings settings;
        try
        {
            settings = MqttSettings.Parse(input);
        }
        catch (InvalidMqttSettingsException exception)
        {
            errors[exception.Field] = new LocalizedText("This value is not valid.", "Este valor não é válido.");
            return ConnectionForm(values, errors);
        }

        // A blank password keeps the stored one only for the same broker and user: admins never see it, so pointing the
        // settings elsewhere (another host or port, TLS off, another user) must not hand it over. Without it the test
        // connects without a password, and a broker that needs one answers with an error on the password field.
        var secret = input.Str(MqttSettings.PasswordField) is { Length: > 0 } given
            ? given
            : context.Existing is { } existing && TryParse(existing.Config) is { } stored && SameBrokerAndUser(stored, settings)
                ? await existing.Secrets.GetAsync(MqttSettings.PasswordField, cancellationToken).ConfigureAwait(false)
                : null;

        await using (var transport = transports(Connection(settings, secret, "setup")))
        {
            try
            {
                await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
                await transport.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (MqttConnectException exception)
            {
                var (field, message) = ConnectError(settings, exception);
                errors[field] = message;
                return ConnectionForm(values, errors);
            }
        }

        var done = new DoneStep(
            context.Existing?.Name ?? $"MQTT ({settings.Host})",
            JsonSerializer.SerializeToElement(values));
        return WithSecret(done, secret);
    }

    private static FormStep ConnectionForm(JsonElement values, IReadOnlyDictionary<string, LocalizedText> errors) =>
        new(
            ConnectionStep,
            new LocalizedText("Connect to the MQTT broker", "Conectar ao broker MQTT"),
            new LocalizedText(
                "ControlHome tests the connection before saving. On Umbrel, use the Mosquitto app's host port (1883) and the user name and password set in that app.",
                "O ControlHome testa a conexão antes de salvar. No Umbrel, use a porta do app Mosquitto (1883) e o usuário e a senha definidos nesse app."),
            MqttConnectorDescriptor.Instance.ConfigFields)
        {
            Values = values,
            Errors = errors,
        };

    private static (string Field, LocalizedText Message) ConnectError(MqttSettings settings, MqttConnectException exception) => exception.Failure switch
    {
        MqttConnectFailure.BadCredentials => (
            MqttSettings.PasswordField,
            new LocalizedText("The broker rejected the user name or password.", "O broker recusou o usuário ou a senha.")),
        // MQTT 3.1.1 brokers (Mosquitto among them) answer a wrong password with "not authorized".
        MqttConnectFailure.NotAuthorized => (
            MqttSettings.PasswordField,
            new LocalizedText(
                "The broker refused this user name and password (not authorized).",
                "O broker recusou este usuário e senha (não autorizado).")),
        MqttConnectFailure.Refused => (
            MqttSettings.ClientIdField,
            new LocalizedText($"The broker refused the connection: {exception.Message}", $"O broker recusou a conexão: {exception.Message}")),
        _ => (
            MqttSettings.HostField,
            new LocalizedText(
                $"Cannot reach the broker at {settings.Endpoint} ({exception.Message}).",
                $"Não foi possível alcançar o broker em {settings.Endpoint} ({exception.Message}).")),
    };

    private static LocalizedText FieldError(ValueErrorKind kind) => kind switch
    {
        ValueErrorKind.Required => new LocalizedText("Required.", "Obrigatório."),
        ValueErrorKind.Unknown => new LocalizedText("Unknown field.", "Campo desconhecido."),
        _ => new LocalizedText("This value is not valid.", "Este valor não é válido."),
    };

    // ---- permit join ------------------------------------------------------------------------------------------------------

    private async Task<FlowStep> StartPermitJoinAsync(FlowContext context, IntegrationInstance existing, JsonElement input, CancellationToken cancellationToken)
    {
        var validation = FieldSchemaValidator.Validate(PermitJoinFields, input);
        if (!validation.IsValid)
        {
            return new AbortStep("invalid_input", new LocalizedText("The pairing time is not valid.", "O tempo de pareamento não é válido."));
        }

        if (TryParse(existing.Config) is not { Zigbee2MqttBaseTopic: { } baseTopic } settings)
        {
            return new AbortStep("zigbee2mqtt_disabled", new LocalizedText("Zigbee2MQTT is not enabled for this integration.", "O Zigbee2MQTT não está ativado nesta integração."));
        }

        var seconds = input.Int(SecondsField) ?? DefaultPermitJoinSeconds;
        var secret = await existing.Secrets.GetAsync(MqttSettings.PasswordField, cancellationToken).ConfigureAwait(false);
        await CloseJoinConnectionAsync().ConfigureAwait(false);
        var transport = transports(Connection(settings, secret, "join"));
        var eventsTopic = $"{baseTopic}/bridge/event";
        transport.MessageReceived = message =>
        {
            if (message.Topic == eventsTopic && Json.TryParse(message.Text) is { ValueKind: JsonValueKind.Object } @event
                && @event.Str("type") is "device_joined" or "device_interview"
                && @event.Get("data") is { ValueKind: JsonValueKind.Object } data
                && (data.Str("friendly_name") ?? data.Str("ieee_address")) is { Length: > 0 } name)
            {
                lock (_lock)
                {
                    if (!_joined.Contains(name))
                    {
                        _joined.Add(name);
                    }
                }
            }

            return Task.CompletedTask;
        };

        try
        {
            await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await transport.SubscribeAsync([eventsTopic], cancellationToken).ConfigureAwait(false);
            var request = Z2mAdapter.PermitJoinRequest(baseTopic, seconds);
            await transport.PublishAsync(request.Topic, request.Payload, retain: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Refused, unreachable, or the connection dropped while subscribing / publishing.
            await transport.DisposeAsync().ConfigureAwait(false);
            var message = exception is MqttConnectException refused
                ? ConnectError(settings, refused).Message
                : new LocalizedText(
                    $"The connection to the broker at {settings.Endpoint} dropped. Try again.",
                    $"A conexão com o broker em {settings.Endpoint} caiu. Tente novamente.");
            return new AbortStep("unreachable", message);
        }
        catch (OperationCanceledException)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        lock (_lock)
        {
            _joinConnection = transport;
            _joinBaseTopic = baseTopic;
            _joinUntil = context.Time.GetUtcNow() + TimeSpan.FromSeconds(seconds);
            _joined.Clear();
        }

        return Progress(TimeSpan.FromSeconds(seconds), []);
    }

    private async Task<FlowStep> PermitJoinProgressAsync(FlowContext context, IntegrationInstance existing, JsonElement input, CancellationToken cancellationToken)
    {
        IMqttTransport? transport;
        string? baseTopic;
        DateTimeOffset until;
        List<string> joined;
        lock (_lock)
        {
            (transport, baseTopic, until, joined) = (_joinConnection, _joinBaseTopic, _joinUntil, [.. _joined]);
        }

        if (transport is null || baseTopic is null)
        {
            return new AbortStep("not_started", new LocalizedText("Pairing has not started.", "O pareamento não foi iniciado."));
        }

        var remaining = until - context.Time.GetUtcNow();
        if (input.Bool(StopField) == true && remaining > TimeSpan.Zero)
        {
            var close = Z2mAdapter.PermitJoinRequest(baseTopic, 0);
            try
            {
                await transport.PublishAsync(close.Topic, close.Payload, retain: false, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The window closes by itself when it times out.
            }

            remaining = TimeSpan.Zero;
        }

        if (remaining > TimeSpan.Zero)
        {
            return Progress(remaining, joined);
        }

        await CloseJoinConnectionAsync().ConfigureAwait(false);
        var secret = await existing.Secrets.GetAsync(MqttSettings.PasswordField, cancellationToken).ConfigureAwait(false);
        return WithSecret(new DoneStep(existing.Name, existing.Config), secret);
    }

    private static ProgressStep Progress(TimeSpan remaining, IReadOnlyList<string> joined)
    {
        var seconds = ((int)Math.Ceiling(remaining.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        var names = string.Join(", ", joined);
        return new ProgressStep(
            PermitJoinProgressStep,
            new LocalizedText("Pair your Zigbee devices now", "Pareie seus dispositivos Zigbee agora"),
            joined.Count == 0
                ? new LocalizedText(
                    $"Zigbee2MQTT accepts new devices for {seconds} s. Put each device in pairing mode (see its manual).",
                    $"O Zigbee2MQTT aceita novos dispositivos por {seconds} s. Coloque cada dispositivo em modo de pareamento (veja o manual).")
                : new LocalizedText(
                    $"Joined so far: {names}. Still accepting devices for {seconds} s.",
                    $"Pareados até agora: {names}. Ainda aceitando dispositivos por {seconds} s."),
            remaining);
    }

    private async Task CloseJoinConnectionAsync()
    {
        IMqttTransport? transport;
        lock (_lock)
        {
            transport = _joinConnection;
            _joinConnection = null;
        }

        if (transport is not null)
        {
            transport.MessageReceived = null;
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------

    /// <summary>A throw-away client id: the integration's own id must stay with the running runtime (a broker drops the older client).</summary>
    private MqttConnectionOptions Connection(MqttSettings settings, string? secret, string purpose) =>
        new(
            settings.Host,
            settings.Port,
            settings.UseTls,
            settings.Username,
            secret,
            $"controlhome-{purpose}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}",
            options.ConnectTimeout);

    /// <summary><paramref name="done"/> carrying the broker password as its only secret (none when there is no password).</summary>
    private static DoneStep WithSecret(DoneStep done, string? secret) =>
        secret is null
            ? done
            : done with { Secrets = new Dictionary<string, string>(StringComparer.Ordinal) { [MqttSettings.PasswordField] = secret } };

    private static bool SameBrokerAndUser(MqttSettings stored, MqttSettings submitted) =>
        string.Equals(stored.Host, submitted.Host, StringComparison.OrdinalIgnoreCase)
        && stored.Port == submitted.Port
        && stored.UseTls == submitted.UseTls
        && string.Equals(stored.Username, submitted.Username, StringComparison.Ordinal);

    private static MqttSettings? TryParse(JsonElement config)
    {
        try
        {
            return MqttSettings.Parse(config);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static JsonElement Defaults() => JsonSerializer.SerializeToElement(new JsonObject
    {
        [MqttSettings.PortField] = MqttSettings.DefaultPort,
        [MqttSettings.UseTlsField] = false,
        [MqttSettings.DiscoveryPrefixField] = MqttSettings.DefaultDiscoveryPrefix,
        [MqttSettings.Zigbee2MqttBaseTopicField] = MqttSettings.DefaultZigbee2MqttBaseTopic,
        [MqttSettings.TasmotaDiscoveryField] = true,
    });

    /// <summary>The submitted (or stored) config without the password member.</summary>
    private static JsonElement WithoutPassword(JsonElement config)
    {
        var copy = config.ValueKind == JsonValueKind.Object ? JsonNode.Parse(config.GetRawText()) as JsonObject ?? [] : [];
        copy.Remove(MqttSettings.PasswordField);
        return JsonSerializer.SerializeToElement(copy);
    }
}

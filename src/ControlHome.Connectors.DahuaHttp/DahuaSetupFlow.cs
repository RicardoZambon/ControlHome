using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.DahuaHttp.Model;
using ControlHome.Connectors.DahuaHttp.Protocol;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Connectors.DahuaHttp;

/// <summary>
/// The connector's flow, for a new integration (one physical device) or an existing one (new password or address): a form
/// with address, port, user, password and device type → identification over the CGI API with Digest authentication
/// (<c>magicBox.cgi?action=getDeviceType</c>, <c>getSoftwareVersion</c>, …; the type is detected when set to auto) →
/// done (the password goes to the secrets, never the config).
/// <para>
/// When the device refuses, a checklist step explains what to check for that answer (401: credentials, Compatibility
/// authentication with MD5, the lockout after failed logins; 500: the authentication mode; 404: the CGI service; 403: IP
/// filter or permission) and offers to try again or edit the settings. A refused login is never retried by the flow on its
/// own: the user must ask again, and the same values (whether through "Try again" or the form) are not sent sooner than
/// <see cref="DahuaRuntimeOptions.FlowAuthRetryCooldown"/> after a refusal (corrected ones are tried at once); after
/// <see cref="DahuaRuntimeOptions.FlowMaxAuthFailures"/> refusals the flow stops before the device locks this server out.
/// A blank password keeps the stored one only for the same address, port and user.
/// </para>
/// </summary>
internal sealed class DahuaSetupFlow(DahuaHttpConnectorDescriptor descriptor, DahuaClientFactory clients, DahuaRuntimeOptions options) : IConnectorFlow
{
    public const string DeviceStep = "device";
    public const string ChecklistStep = "checklist";
    public const string RetryChoice = "retry";
    public const string EditChoice = "edit";

    private FlowContext? _context;
    private JsonElement _pendingValues;
    private string? _pendingPassword;
    private DahuaFailure? _lastFailure;
    private DateTimeOffset _lastAuthFailureAt;
    private (string Host, int Port, string Username, string Password)? _refusedAttempt;
    private int _authFailures;

    public Task<FlowStep> StartAsync(FlowContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        var values = context.Existing is { } existing ? WithoutPassword(existing.Config) : Defaults(context.Language);
        return Task.FromResult<FlowStep>(DeviceForm(values, new Dictionary<string, LocalizedText>()));
    }

    public async Task<FlowStep> ContinueAsync(string stepId, JsonElement input, CancellationToken cancellationToken)
    {
        var context = _context ?? throw new InvalidOperationException("The flow has not started.");
        switch (stepId)
        {
            case DeviceStep:
                return await TestAsync(context, input, cancellationToken).ConfigureAwait(false);
            case ChecklistStep when _lastFailure is not null:
                var choice = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("choice", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString()
                    : null;
                if (choice == EditChoice)
                {
                    return DeviceForm(_pendingValues, new Dictionary<string, LocalizedText>());
                }

                if (choice != RetryChoice)
                {
                    return new AbortStep("unknown_choice", new LocalizedText("Unknown choice.", "Opção desconhecida."));
                }

                // The same values again: TestAsync holds a refused login back until the cooldown has passed.
                return await TestAsync(context, _pendingValues, _pendingPassword, cancellationToken).ConfigureAwait(false);
            default:
                return new AbortStep("unknown_step", new LocalizedText("This step is not part of the flow.", "Esta etapa não faz parte do fluxo."));
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<FlowStep> TestAsync(FlowContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var values = WithoutPassword(input);
        var validation = FieldSchemaValidator.Validate(descriptor.ConfigFields, input);
        if (!validation.IsValid)
        {
            var errors = new Dictionary<string, LocalizedText>(StringComparer.Ordinal);
            foreach (var error in validation.Errors)
            {
                errors.TryAdd(error.Argument ?? DahuaSettings.HostField, FieldError(error.Kind));
            }

            return DeviceForm(values, errors);
        }

        var given = input.TryGetProperty(DahuaSettings.PasswordField, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } text
            ? text
            : null;
        return await TestAsync(context, values, given, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FlowStep> TestAsync(FlowContext context, JsonElement values, string? givenPassword, CancellationToken cancellationToken)
    {
        DahuaSettings settings;
        try
        {
            settings = DahuaSettings.Parse(values, descriptor);
        }
        catch (InvalidDahuaSettingsException exception)
        {
            return DeviceForm(values, new Dictionary<string, LocalizedText>
            {
                [exception.Field] = new("This value is not valid.", "Este valor não é válido."),
            });
        }

        // A blank password keeps the stored one only for the same device and user: admins never see it, so pointing the
        // settings elsewhere must not hand it over.
        var password = givenPassword
            ?? (context.Existing is { } existing && TryParse(existing.Config) is { } stored && SameDevice(stored, settings)
                ? await existing.Secrets.GetAsync(DahuaSettings.PasswordField, cancellationToken).ConfigureAwait(false)
                : null);
        if (string.IsNullOrEmpty(password))
        {
            return DeviceForm(values, new Dictionary<string, LocalizedText>
            {
                [DahuaSettings.PasswordField] = new("Required.", "Obrigatório."),
            });
        }

        // A refused login is not sent again with nothing changed (a double click, "Change the settings" and back) before
        // the cooldown: each refusal counts towards the device's lockout. Corrected values are tried at once.
        var attempt = (settings.Host.ToUpperInvariant(), settings.Port, settings.Username, password);
        if (_refusedAttempt == attempt && _lastAuthFailureAt + options.FlowAuthRetryCooldown - context.Time.GetUtcNow() is var wait && wait > TimeSpan.Zero)
        {
            _lastFailure = DahuaFailure.Unauthorized;
            return Checklist(DahuaFailure.Unauthorized, wait);
        }

        _pendingValues = values;
        _pendingPassword = givenPassword;
        using var client = clients(new DahuaConnection(settings.BaseUri, settings.Username, password, context.Time));
        DahuaDeviceKind kind;
        string name;
        try
        {
            if (settings.Kind == DahuaDeviceKind.XpeIntercom)
            {
                // The relay cannot be tested without opening the door: only check that the address answers HTTP.
                await client.ProbeAsync(cancellationToken).ConfigureAwait(false);
                kind = DahuaDeviceKind.XpeIntercom;
                name = $"XPE ({settings.Host})";
            }
            else
            {
                var identity = await client.IdentifyAsync(cancellationToken).ConfigureAwait(false);
                kind = settings.Kind;
                if (kind == DahuaDeviceKind.Auto)
                {
                    var channels = DahuaKindDetector.Detect(identity, 0) == DahuaDeviceKind.AccessControl
                        ? 0
                        : (await client.GetChannelTitlesAsync(cancellationToken).ConfigureAwait(false)).Count;
                    kind = DahuaKindDetector.Detect(identity, channels);
                }

                name = $"{identity.MachineName ?? identity.DeviceType} ({settings.Host})";
            }
        }
        catch (DahuaHttpException exception) when (exception.Failure is DahuaFailure.Unreachable or DahuaFailure.Timeout)
        {
            return DeviceForm(values, new Dictionary<string, LocalizedText>
            {
                [DahuaSettings.HostField] = new(
                    $"Cannot reach the device at {settings.Endpoint}. Check the address, the port and that it is on.",
                    $"Não foi possível alcançar o dispositivo em {settings.Endpoint}. Confira o endereço, a porta e se ele está ligado."),
            });
        }
        catch (DahuaHttpException exception)
        {
            _lastFailure = exception.Failure;
            if (exception.Failure == DahuaFailure.Unauthorized)
            {
                _authFailures++;
                _lastAuthFailureAt = context.Time.GetUtcNow();
                _refusedAttempt = attempt;
                if (_authFailures >= options.FlowMaxAuthFailures)
                {
                    return new AbortStep(
                        "too_many_failed_logins",
                        new LocalizedText(
                            "The device refused the login several times. Wait 30 minutes before trying again (devices lock out after repeated failures), then check the user and password in the device's own web page.",
                            "O dispositivo recusou o login várias vezes. Aguarde 30 minutos antes de tentar de novo (os dispositivos bloqueiam após falhas repetidas) e confira o usuário e a senha na própria página web do dispositivo."));
                }
            }

            return Checklist(exception.Failure, TimeSpan.Zero);
        }

        var config = JsonNode.Parse(values.GetRawText()) as JsonObject ?? [];
        config[DahuaSettings.KindField] = DahuaSettings.KindName(kind);
        config[DahuaSettings.LanguageField] ??= context.Language == "pt-BR" ? "pt-BR" : "en";
        var done = new DoneStep(context.Existing?.Name ?? name, JsonSerializer.SerializeToElement(config))
        {
            Secrets = new Dictionary<string, string>(StringComparer.Ordinal) { [DahuaSettings.PasswordField] = password },
        };
        return done;
    }

    private FormStep DeviceForm(JsonElement values, IReadOnlyDictionary<string, LocalizedText> errors) =>
        new(
            DeviceStep,
            new LocalizedText("Connect to the device", "Conectar ao dispositivo"),
            new LocalizedText(
                "ControlHome talks to Intelbras cameras, recorders (NVD, MHDX) and SS access terminals on your network over their HTTP API. It checks the connection before saving.",
                "O ControlHome conversa com câmeras, gravadores (NVD, MHDX) e terminais de acesso SS da Intelbras na sua rede pela API HTTP deles. A conexão é testada antes de salvar."),
            descriptor.ConfigFields)
        {
            Values = values,
            Errors = errors,
        };

    private static ChooseStep Checklist(DahuaFailure failure, TimeSpan wait)
    {
        var (en, pt) = failure switch
        {
            DahuaFailure.Unauthorized => (
                "The device refused the user name or password (HTTP 401). Check:\n"
                + "1. The user and password: log in with them on the device's own web page.\n"
                + "2. Authentication: in the device's security settings, set the authentication mode to Compatibility (compatível) and allow MD5 digest.\n"
                + "3. Lockout: devices lock out an address after a few failed logins (often 30 minutes). ControlHome never retries on its own; wait before trying again.",
                "O dispositivo recusou o usuário ou a senha (HTTP 401). Confira:\n"
                + "1. O usuário e a senha: entre com eles na própria página web do dispositivo.\n"
                + "2. Autenticação: nas configurações de segurança do dispositivo, deixe o modo de autenticação em Compatível e permita o digest MD5.\n"
                + "3. Bloqueio: os dispositivos bloqueiam um endereço após algumas falhas de login (muitas vezes por 30 minutos). O ControlHome nunca tenta de novo sozinho; aguarde antes de tentar."),
            DahuaFailure.ServerError => (
                "The device answered with an internal error (HTTP 500). This usually means the authentication mode: set it to Compatibility (compatível) and enable MD5 among the digest algorithms (only MD5 if possible: several algorithms at once can break RTSP). Then try again.",
                "O dispositivo respondeu com um erro interno (HTTP 500). Normalmente é o modo de autenticação: deixe-o em Compatível e ative o MD5 entre os algoritmos de digest (só o MD5, se possível: vários algoritmos juntos podem quebrar o RTSP). Depois tente de novo."),
            DahuaFailure.NotFound => (
                "The device has no CGI API at this address (HTTP 404). Enable the CGI service (on SS access terminals: Security > Services > CGI) and check the HTTP port.",
                "O dispositivo não tem a API CGI neste endereço (HTTP 404). Ative o serviço CGI (nos terminais de acesso SS: Segurança > Serviços > CGI) e confira a porta HTTP."),
            DahuaFailure.Forbidden => (
                "The device refused access (HTTP 403). The user may lack permissions, or an IP filter (allow list) blocks this server: add ControlHome's IP address to it.",
                "O dispositivo recusou o acesso (HTTP 403). O usuário pode não ter permissão, ou um filtro de IP (lista de permitidos) bloqueia este servidor: inclua nele o endereço IP do ControlHome."),
            _ => (
                "The device answered, but not as a supported Intelbras/Dahua device. Check the address and the port, and that the firmware has the HTTP (CGI) API.",
                "O dispositivo respondeu, mas não como um dispositivo Intelbras/Dahua compatível. Confira o endereço e a porta, e se o firmware tem a API HTTP (CGI)."),
        };
        if (wait > TimeSpan.Zero)
        {
            var seconds = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            en += $"\nWait {seconds} s before trying again.";
            pt += $"\nAguarde {seconds} s antes de tentar de novo.";
        }

        return new ChooseStep(
            ChecklistStep,
            new LocalizedText("The device refused the connection", "O dispositivo recusou a conexão"),
            new LocalizedText(en, pt),
            [
                new FlowChoice(RetryChoice, new LocalizedText("Try again", "Tentar de novo"), null),
                new FlowChoice(EditChoice, new LocalizedText("Change the settings", "Alterar as configurações"), null),
            ]);
    }

    private static LocalizedText FieldError(ValueErrorKind kind) => kind switch
    {
        ValueErrorKind.Required => new LocalizedText("Required.", "Obrigatório."),
        ValueErrorKind.Unknown => new LocalizedText("Unknown field.", "Campo desconhecido."),
        _ => new LocalizedText("This value is not valid.", "Este valor não é válido."),
    };

    private static bool SameDevice(DahuaSettings stored, DahuaSettings submitted) =>
        string.Equals(stored.Host, submitted.Host, StringComparison.OrdinalIgnoreCase)
        && stored.Port == submitted.Port
        && string.Equals(stored.Username, submitted.Username, StringComparison.Ordinal);

    private DahuaSettings? TryParse(JsonElement config)
    {
        try
        {
            return DahuaSettings.Parse(config, descriptor);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static JsonElement Defaults(string language) => JsonSerializer.SerializeToElement(new JsonObject
    {
        [DahuaSettings.PortField] = DahuaSettings.DefaultPort,
        [DahuaSettings.UsernameField] = DahuaSettings.DefaultUsername,
        [DahuaSettings.KindField] = "auto",
        [DahuaSettings.RtspPortField] = DahuaSettings.DefaultRtspPort,
        [DahuaSettings.LanguageField] = language == "pt-BR" ? "pt-BR" : "en",
    });

    /// <summary>The submitted (or stored) config without the password member.</summary>
    private static JsonElement WithoutPassword(JsonElement config)
    {
        var copy = config.ValueKind == JsonValueKind.Object ? JsonNode.Parse(config.GetRawText()) as JsonObject ?? [] : [];
        copy.Remove(DahuaSettings.PasswordField);
        return JsonSerializer.SerializeToElement(copy);
    }
}

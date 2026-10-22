using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.SmartThings.Api;
using ControlHome.Connectors.SmartThings.Auth;
using ControlHome.Connectors.SmartThings.Runtime;
using ControlHome.Domain.Capabilities;
using Microsoft.Extensions.Logging;

namespace ControlHome.Connectors.SmartThings.Flow;

/// <summary>
/// Setup and re-authorization. OAuth (recommended): instructions to create the household's own OAuth-In app (data:
/// <see cref="SetupInstructions"/>) → the app's client id/secret, redirect URL and OAuth URL preset → the SmartThings
/// sign-in page (authorize URL with a random <c>state</c>) and the pasted-back redirected URL (its state must match: CSRF
/// protection) → the code exchange (HTTP Basic client authentication) → the location (asked only when the token sees
/// several) → done. Personal access token (development only, 24 h): token → location → done. Re-authorizing keeps the
/// integration's location (and with it every device); a token for another location is refused, and so is a location
/// another running integration already reads with the same app (both would hold the same single-use tokens). When the
/// locations cannot be read right after the code exchange (the code is spent by then), only that read is retried.
/// Answering the first step again starts over: nothing of the other sign-in mode is kept.
/// </summary>
internal sealed partial class SmartThingsSetupFlow(Func<HttpClient> http, SmartThingsOptions options, SetupInstructions instructions, LocationClaims locations)
    : IConnectorFlow
{
    public const string ModeStep = "mode";
    public const string InstructionsStep = "instructions";
    public const string AppStep = "app";
    public const string AuthorizeStep = "authorize";
    public const string TokenStep = "token";
    public const string LocationStep = "location";
    public const string LocationsRetryStep = "locationsRetry";
    public const string RedirectedUrlField = "redirectedUrl";

    /// <summary>Pasted URLs whose state does not match before the flow gives up.</summary>
    public const int MaxStateMismatches = 5;

    private FlowContext? _context;
    private HttpClient? _http;
    private OAuthApp? _app;
    private string? _redirectUri;
    private string _preset = SmartThingsSettings.DocumentedPreset;
    private string? _customAuthorizeUrl;
    private string? _customTokenUrl;
    private string? _state;
    private int _mismatches;
    private SmartThingsAuthMode _mode;
    private TokenSet? _tokens;
    private string? _pat;
    private IReadOnlyList<SmartThingsLocation> _locations = [];

    /// <summary>The <c>state</c> of the current sign-in (tests).</summary>
    internal string? State => _state;

    public Task<FlowStep> StartAsync(FlowContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        return Task.FromResult<FlowStep>(ModeChoice(context.Existing is not null));
    }

    public async Task<FlowStep> ContinueAsync(string stepId, JsonElement input, CancellationToken cancellationToken)
    {
        var context = _context ?? throw new InvalidOperationException("The flow has not started.");
        switch (stepId)
        {
            case ModeStep:
                // Starting over: a sign-in, token or location list of the other mode must never finish this one.
                Reset();
                switch (input.Str("choice"))
                {
                    case SmartThingsSettings.OAuthMode:
                        _mode = SmartThingsAuthMode.OAuth;
                        return new ExternalUrlStep(InstructionsStep, instructions.Title, instructions.Description, instructions.DocsUrl, null);
                    case SmartThingsSettings.PatMode:
                        _mode = SmartThingsAuthMode.PersonalAccessToken;
                        return TokenForm(new Dictionary<string, LocalizedText>());
                    default:
                        return new AbortStep("unknown_choice", new LocalizedText("Unknown choice.", "Opção desconhecida."));
                }

            case InstructionsStep when _mode == SmartThingsAuthMode.OAuth:
                return AppForm(AppDefaults(context), new Dictionary<string, LocalizedText>());
            case AppStep when _mode == SmartThingsAuthMode.OAuth:
                return await SubmitAppAsync(context, input, cancellationToken).ConfigureAwait(false);
            case AuthorizeStep when _mode == SmartThingsAuthMode.OAuth && _app is not null && _state is not null:
                return await SubmitRedirectAsync(context, input, cancellationToken).ConfigureAwait(false);
            case TokenStep when _mode == SmartThingsAuthMode.PersonalAccessToken:
                return await SubmitTokenAsync(context, input, cancellationToken).ConfigureAwait(false);
            case LocationsRetryStep when _mode == SmartThingsAuthMode.OAuth && _app is not null && _tokens is not null:
                return await LocationsAsync(context, new PersonalAccessToken(_tokens.AccessToken), tokenField: null, cancellationToken).ConfigureAwait(false);
            case LocationStep when _locations.Count > 0 && HasCredentials:
                return input.Str("choice") is { } choice && _locations.FirstOrDefault(l => l.Id == choice) is { } location
                    ? Done(context, location)
                    : new AbortStep("unknown_choice", new LocalizedText("Unknown location.", "Local desconhecido."));
            default:
                return new AbortStep("unknown_step", new LocalizedText("This step is not part of the flow.", "Esta etapa não faz parte do fluxo."));
        }
    }

    public ValueTask DisposeAsync()
    {
        _http?.Dispose();
        _http = null;
        return ValueTask.CompletedTask;
    }

    /// <summary>Whether the credentials of the current mode are in hand (a location choice can finish the flow).</summary>
    private bool HasCredentials => _mode == SmartThingsAuthMode.OAuth ? _app is not null && _tokens is not null : _pat is not null;

    private void Reset()
    {
        _app = null;
        _redirectUri = null;
        _preset = SmartThingsSettings.DocumentedPreset;
        _customAuthorizeUrl = null;
        _customTokenUrl = null;
        _state = null;
        _mismatches = 0;
        _tokens = null;
        _pat = null;
        _locations = [];
    }

    // ---- steps --------------------------------------------------------------------------------------------------------

    private static ChooseStep ModeChoice(bool existing) => new(
        ModeStep,
        new LocalizedText("Connect SmartThings", "Conectar a SmartThings"),
        existing
            ? new LocalizedText(
                "Sign in again. Devices, rooms and automations of this integration are kept.",
                "Entre novamente. Os dispositivos, cômodos e automações desta integração são mantidos.")
            : new LocalizedText(
                "ControlHome reads and controls the devices of one SmartThings location through Samsung's cloud.",
                "O ControlHome lê e controla os dispositivos de um local da SmartThings pela nuvem da Samsung."),
        [
            new FlowChoice(
                SmartThingsSettings.OAuthMode,
                new LocalizedText("Your own SmartThings app (recommended)", "O seu próprio app SmartThings (recomendado)"),
                new LocalizedText(
                    "Create an OAuth app with the SmartThings CLI once; ControlHome then keeps its tokens fresh by itself.",
                    "Crie uma vez um app OAuth com a CLI da SmartThings; depois o ControlHome mantém os tokens válidos sozinho.")),
            new FlowChoice(
                SmartThingsSettings.PatMode,
                new LocalizedText("Personal access token (development only)", "Token de acesso pessoal (só para desenvolvimento)"),
                new LocalizedText(
                    "Expires 24 hours after you created it; then the integration asks you to sign in again.",
                    "Expira 24 horas depois de criado; então a integração pede para entrar novamente.")),
        ]);

    private IReadOnlyList<FieldSchema> AppFields(bool existing) =>
    [
        new FieldSchema(
            SmartThingsSettings.ClientIdField,
            new LocalizedText("OAuth client id", "Client id do OAuth"),
            new StringSchema(128),
            Required: true,
            Secret: false,
            new LocalizedText("Printed by the SmartThings CLI when it created your app.", "Mostrado pela CLI da SmartThings quando criou o seu app.")),
        new FieldSchema(
            SmartThingsSettings.ClientSecretField,
            new LocalizedText("OAuth client secret", "Client secret do OAuth"),
            new StringSchema(256),
            Required: !existing,
            Secret: true,
            existing
                ? new LocalizedText("Leave empty to keep the stored secret (same client id only).", "Deixe vazio para manter o secret guardado (só com o mesmo client id).")
                : new LocalizedText("Printed once by the CLI. Stored encrypted; never shown again.", "Mostrado uma única vez pela CLI. Guardado criptografado; nunca mais exibido.")),
        new FieldSchema(
            SmartThingsSettings.RedirectUriField,
            new LocalizedText("Redirect URL", "URL de redirecionamento"),
            new StringSchema(2048),
            Required: true,
            Secret: false,
            new LocalizedText(
                $"Exactly the redirect URI registered in your app. With {instructions.DefaultRedirectUri} the third-party site httpbin.org receives the one-time sign-in code (useless without your client secret, which stays here); use an HTTPS page you control if you prefer.",
                $"Exatamente a redirect URI registrada no seu app. Com {instructions.DefaultRedirectUri} o site de terceiros httpbin.org recebe o código de acesso de uso único (inútil sem o seu client secret, que fica aqui); use uma página HTTPS sua se preferir.")),
        new FieldSchema(
            SmartThingsSettings.EndpointsField,
            new LocalizedText("OAuth URLs", "URLs do OAuth"),
            new EnumSchema([SmartThingsSettings.DocumentedPreset, SmartThingsSettings.HomeAssistantPreset, SmartThingsSettings.CustomPreset]),
            Required: true,
            Secret: false,
            new LocalizedText(
                "documented: api.smartthings.com/v1/oauth/…; homeAssistant: the URLs Home Assistant uses; custom: the two URLs below (HTTPS on smartthings.com). Try another preset if sign-in fails.",
                "documented: api.smartthings.com/v1/oauth/…; homeAssistant: as URLs que o Home Assistant usa; custom: as duas URLs abaixo (HTTPS em smartthings.com). Tente outro preset se o acesso falhar.")),
        new FieldSchema(
            SmartThingsSettings.AuthorizeUrlField,
            new LocalizedText("Authorize URL (custom)", "URL de autorização (custom)"),
            new StringSchema(2048),
            Required: false,
            Secret: false,
            null) { Advanced = true },
        new FieldSchema(
            SmartThingsSettings.TokenUrlField,
            new LocalizedText("Token URL (custom)", "URL do token (custom)"),
            new StringSchema(2048),
            Required: false,
            Secret: false,
            null) { Advanced = true },
    ];

    private FormStep AppForm(JsonElement values, IReadOnlyDictionary<string, LocalizedText> errors) => new(
        AppStep,
        new LocalizedText("Your SmartThings app", "O seu app SmartThings"),
        new LocalizedText(
            "SmartThings will send your browser to the redirect URL with a one-time code in its address; you then paste that address back here. Redirects to localhost or LAN addresses are reported not to work.",
            "A SmartThings vai mandar o seu navegador para a URL de redirecionamento com um código de uso único no endereço; depois você cola esse endereço aqui. Redirecionamentos para localhost ou endereços da rede local não funcionam, segundo relatos."),
        AppFields(_context?.Existing is not null))
    {
        Values = values,
        Errors = errors,
    };

    private JsonElement AppDefaults(FlowContext context)
    {
        var values = new JsonObject
        {
            [SmartThingsSettings.RedirectUriField] = instructions.DefaultRedirectUri,
            [SmartThingsSettings.EndpointsField] = SmartThingsSettings.DocumentedPreset,
        };
        if (context.Existing is { } existing && existing.Config.ValueKind == JsonValueKind.Object && existing.Config.Str(SmartThingsSettings.ModeField) == SmartThingsSettings.OAuthMode)
        {
            foreach (var field in (string[])[SmartThingsSettings.ClientIdField, SmartThingsSettings.RedirectUriField, SmartThingsSettings.EndpointsField, SmartThingsSettings.AuthorizeUrlField, SmartThingsSettings.TokenUrlField])
            {
                if (existing.Config.Str(field) is { } value)
                {
                    values[field] = value;
                }
            }
        }

        return JsonSerializer.SerializeToElement(values);
    }

    private async Task<FlowStep> SubmitAppAsync(FlowContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, LocalizedText>(StringComparer.Ordinal);
        var values = Without(input, SmartThingsSettings.ClientSecretField);
        var fields = AppFields(context.Existing is not null);
        var validation = FieldSchemaValidator.Validate(fields, input);
        foreach (var error in validation.Errors)
        {
            errors.TryAdd(error.Argument ?? SmartThingsSettings.ClientIdField, FieldError(error.Kind));
        }

        if (errors.Count > 0)
        {
            return AppForm(values, errors);
        }

        var clientId = input.Str(SmartThingsSettings.ClientIdField)!.Trim();
        if (!SmartThingsSettings.IsClientId(clientId))
        {
            errors[SmartThingsSettings.ClientIdField] = new LocalizedText("This is not a client id.", "Isto não é um client id.");
        }

        var redirect = input.Str(SmartThingsSettings.RedirectUriField)!.Trim();
        if (!SmartThingsSettings.IsRedirectUri(redirect))
        {
            errors[SmartThingsSettings.RedirectUriField] = new LocalizedText(
                "Enter an absolute https:// URL without a #fragment.",
                "Informe uma URL https:// completa, sem #fragmento.");
        }

        var preset = input.Str(SmartThingsSettings.EndpointsField) ?? SmartThingsSettings.DocumentedPreset;
        var authorizeUrl = input.Str(SmartThingsSettings.AuthorizeUrlField);
        var tokenUrl = input.Str(SmartThingsSettings.TokenUrlField);
        var endpoints = SmartThingsSettings.ResolveEndpoints(preset, authorizeUrl, tokenUrl, options, out var invalidField);
        if (endpoints is null)
        {
            errors[invalidField ?? SmartThingsSettings.EndpointsField] = new LocalizedText(
                "Enter an https:// URL on smartthings.com.",
                "Informe uma URL https:// em smartthings.com.");
        }

        // A blank secret keeps the stored one only for the same app: admins never see it, so another client id never gets it.
        var secret = input.Str(SmartThingsSettings.ClientSecretField) is { Length: > 0 } typed
            ? typed.Trim()
            : context.Existing is { } existing && string.Equals(existing.Config.Str(SmartThingsSettings.ClientIdField), clientId, StringComparison.Ordinal)
                ? await existing.Secrets.GetAsync(SecretKeys.ClientSecret, cancellationToken).ConfigureAwait(false)
                : null;
        if (string.IsNullOrEmpty(secret))
        {
            errors[SmartThingsSettings.ClientSecretField] = new LocalizedText("Required.", "Obrigatório.");
        }

        if (errors.Count > 0)
        {
            return AppForm(values, errors);
        }

        // Another app: tokens and locations of an earlier sign-in (with the previous app) never finish this one.
        _tokens = null;
        _locations = [];
        _app = new OAuthApp(clientId, secret!, endpoints!);
        _redirectUri = redirect;
        _preset = preset;
        _customAuthorizeUrl = preset == SmartThingsSettings.CustomPreset ? authorizeUrl?.Trim() : null;
        _customTokenUrl = preset == SmartThingsSettings.CustomPreset ? tokenUrl?.Trim() : null;
        return NewSignIn(null);
    }

    /// <summary>The sign-in page with a fresh state (a new state each time: a pasted URL from an older attempt is refused).</summary>
    private ExternalUrlStep NewSignIn(LocalizedText? problem)
    {
        _state = AuthorizationRequest.NewState();
        _mismatches = 0;
        return SignIn(problem);
    }

    private ExternalUrlStep SignIn(LocalizedText? problem)
    {
        var url = AuthorizationRequest.BuildUrl(_app!.Endpoints, _app.ClientId, _redirectUri!, instructions.Scopes, _state!);
        var text = new LocalizedText(
            $"Open the SmartThings page, sign in with your Samsung account, choose the location for this integration and allow access. Your browser then opens {_redirectUri}: copy the whole address from the address bar (on httpbin.org you can also copy the page's text) and paste it below.",
            $"Abra a página da SmartThings, entre com a sua conta Samsung, escolha o local desta integração e permita o acesso. O navegador então abre {_redirectUri}: copie o endereço inteiro da barra de endereços (no httpbin.org você também pode copiar o texto da página) e cole abaixo.");
        return new ExternalUrlStep(
            AuthorizeStep,
            new LocalizedText("Sign in to SmartThings", "Entre na SmartThings"),
            problem is null ? text : new LocalizedText(problem.En + " " + text.En, problem.PtBr + " " + text.PtBr),
            url,
            new FieldSchema(
                RedirectedUrlField,
                new LocalizedText("Address of the page you were sent to", "Endereço da página para onde você foi enviado"),
                new StringSchema(8192),
                Required: true,
                Secret: true,
                new LocalizedText("It contains code=… and state=…", "Ele contém code=… e state=…")));
    }

    private async Task<FlowStep> SubmitRedirectAsync(FlowContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var pasted = AuthorizationRequest.Parse(input.Str(RedirectedUrlField));
        if (pasted is null)
        {
            return SignIn(new LocalizedText(
                "That address has no sign-in code: copy the whole address of the page SmartThings sent you to.",
                "Esse endereço não tem código de acesso: copie o endereço inteiro da página para onde a SmartThings enviou você."));
        }

        // CSRF protection: only the answer to THIS flow's sign-in is accepted.
        if (!AuthorizationRequest.StateMatches(_state!, pasted.State))
        {
            LogStateMismatch(context.Logger, pasted.State is null);
            if (++_mismatches >= MaxStateMismatches)
            {
                return new AbortStep("state_mismatch", new LocalizedText(
                    "The pasted addresses did not belong to this sign-in. Start again.",
                    "Os endereços colados não pertenciam a este acesso. Comece de novo."));
            }

            return SignIn(new LocalizedText(
                "That address does not belong to this sign-in (its state does not match). Use the button below and paste the address you are sent to afterwards.",
                "Esse endereço não pertence a este acesso (o state não confere). Use o botão abaixo e cole o endereço para onde você for enviado depois."));
        }

        if (pasted.Error is { } error)
        {
            return NewSignIn(new LocalizedText(
                $"SmartThings did not grant access ({SmartThingsErrors.Sanitize(error)}).",
                $"A SmartThings não concedeu o acesso ({SmartThingsErrors.Sanitize(error)})."));
        }

        try
        {
            var oauth = new OAuthClient(Http(), context.Time, options.RequestTimeout);
            _tokens = await oauth.ExchangeCodeAsync(_app!, pasted.Code!, _redirectUri!, cancellationToken).ConfigureAwait(false);
        }
        catch (SmartThingsApiException exception)
        {
            LogExchangeFailed(context.Logger, exception.Kind, exception.VendorCode is null ? null : SmartThingsErrors.Sanitize(exception.VendorCode));
            return exception switch
            {
                { VendorCode: "invalid_request" or "redirect_uri_mismatch" } => AppForm(
                    AppValues(),
                    new Dictionary<string, LocalizedText>(StringComparer.Ordinal)
                    {
                        [SmartThingsSettings.RedirectUriField] = new LocalizedText(
                            "SmartThings refused the request: the redirect URL must be exactly the one registered in your app.",
                            "A SmartThings recusou o pedido: a URL de redirecionamento precisa ser exatamente a registrada no seu app."),
                    }),
                { VendorCode: "invalid_client" or "unauthorized_client" } or { Status: System.Net.HttpStatusCode.Unauthorized } => AppForm(
                    AppValues(),
                    new Dictionary<string, LocalizedText>(StringComparer.Ordinal)
                    {
                        [SmartThingsSettings.ClientSecretField] = new LocalizedText(
                            "SmartThings rejected this client id or secret.",
                            "A SmartThings recusou este client id ou secret."),
                    }),
                { Kind: ConnectorErrorKind.Unauthorized or ConnectorErrorKind.Permanent } => NewSignIn(new LocalizedText(
                    "That sign-in code can no longer be used (it expires after a few minutes and works once). Sign in again.",
                    "Esse código de acesso não pode mais ser usado (expira em poucos minutos e vale uma vez). Entre novamente.")),
                { Kind: ConnectorErrorKind.PlanRequired } => PlanRequired(),
                _ => SignIn(new LocalizedText(
                    "SmartThings could not be reached. Paste the address again in a moment.",
                    "Não foi possível falar com a SmartThings. Cole o endereço de novo daqui a pouco.")),
            };
        }

        _state = null;
        return await LocationsAsync(context, new PersonalAccessToken(_tokens.AccessToken), tokenField: null, cancellationToken).ConfigureAwait(false);
    }

    private FormStep TokenForm(IReadOnlyDictionary<string, LocalizedText> errors) => new(
        TokenStep,
        new LocalizedText("Personal access token (development only)", "Token de acesso pessoal (só para desenvolvimento)"),
        new LocalizedText(
            "Create a token at account.smartthings.com/tokens with the scopes r:locations:*, r:devices:* and x:devices:*. Tokens created since 30 Dec 2024 expire 24 hours after they were created: the integration then stops and asks you to sign in again. Use your own SmartThings app for everyday use.",
            "Crie um token em account.smartthings.com/tokens com os escopos r:locations:*, r:devices:* e x:devices:*. Tokens criados desde 30/12/2024 expiram 24 horas depois de criados: a integração então para e pede para entrar novamente. Para o dia a dia, use o seu próprio app SmartThings."),
        [
            new FieldSchema(
                SmartThingsSettings.PersonalAccessTokenField,
                new LocalizedText("Personal access token", "Token de acesso pessoal"),
                new StringSchema(256),
                Required: true,
                Secret: true,
                null),
        ])
    {
        Errors = errors,
    };

    private async Task<FlowStep> SubmitTokenAsync(FlowContext context, JsonElement input, CancellationToken cancellationToken)
    {
        var token = input.Str(SmartThingsSettings.PersonalAccessTokenField)?.Trim();
        if (string.IsNullOrEmpty(token) || token.Length > 256 || token.Any(char.IsWhiteSpace))
        {
            return TokenForm(new Dictionary<string, LocalizedText>(StringComparer.Ordinal)
            {
                [SmartThingsSettings.PersonalAccessTokenField] = new LocalizedText("Paste the token.", "Cole o token."),
            });
        }

        _pat = token;
        return await LocationsAsync(context, new PersonalAccessToken(token), SmartThingsSettings.PersonalAccessTokenField, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The token's locations: one → done; several → choose; re-authorization → the integration's own location only.</summary>
    /// <param name="tokenField">The form field to blame for a rejected token (personal access token), or <c>null</c> (OAuth).</param>
    private async Task<FlowStep> LocationsAsync(FlowContext context, IAccessTokenSource token, string? tokenField, CancellationToken cancellationToken)
    {
        try
        {
            var api = new SmartThingsApi(Http(), options with { GetRetries = 0 }, token, context.Time, new RateLimitTracker());
            _locations = await api.ListLocationsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SmartThingsApiException exception)
        {
            LogLocationsFailed(context.Logger, exception.Kind);
            if (exception.Kind == ConnectorErrorKind.PlanRequired)
            {
                return PlanRequired();
            }

            var message = exception.Kind switch
            {
                ConnectorErrorKind.Unauthorized => new LocalizedText("SmartThings rejected this token (expired, revoked or mistyped).", "A SmartThings recusou este token (expirado, revogado ou digitado errado)."),
                ConnectorErrorKind.Permanent => new LocalizedText("The token may not read locations: it needs the r:locations:* scope.", "O token não pode ler locais: ele precisa do escopo r:locations:*."),
                _ => new LocalizedText("SmartThings could not be reached. Try again in a moment.", "Não foi possível falar com a SmartThings. Tente de novo daqui a pouco."),
            };
            if (tokenField is not null)
            {
                return TokenForm(new Dictionary<string, LocalizedText>(StringComparer.Ordinal) { [tokenField] = message });
            }

            // OAuth: the single-use code is spent and the tokens are in hand: a passing outage only needs the read again.
            return exception.Kind is ConnectorErrorKind.Transient or ConnectorErrorKind.RateLimited
                ? LocationsRetry()
                : new AbortStep("locations_failed", message);
        }

        if (_locations.Count == 0)
        {
            return new AbortStep("no_location", new LocalizedText(
                "This sign-in gives access to no SmartThings location.",
                "Este acesso não dá acesso a nenhum local da SmartThings."));
        }

        if (context.Existing is { } existing && existing.Config.Str(SmartThingsSettings.LocationIdField) is { } current)
        {
            // Re-authorizing keeps the location: the devices belong to it.
            return _locations.FirstOrDefault(l => l.Id == current) is { } same
                ? Done(context, same)
                : new AbortStep("location_mismatch", new LocalizedText(
                    "This sign-in is for another SmartThings location. Add that location as a new integration; this one stays with its own.",
                    "Este acesso é de outro local da SmartThings. Adicione esse local como uma nova integração; esta continua com o seu."));
        }

        if (_locations.Count == 1)
        {
            return Done(context, _locations[0]);
        }

        return new ChooseStep(
            LocationStep,
            new LocalizedText("Choose the location", "Escolha o local"),
            new LocalizedText(
                "One integration controls one SmartThings location; add another integration for each other location.",
                "Uma integração controla um local da SmartThings; adicione outra integração para cada outro local."),
            [.. _locations.Select(l => new FlowChoice(l.Id, new LocalizedText(l.Name, l.Name), null))]);
    }

    private static FormStep LocationsRetry() => new(
        LocationsRetryStep,
        new LocalizedText("Signed in to SmartThings", "Acesso à SmartThings concluído"),
        new LocalizedText(
            "You are signed in, but SmartThings could not be reached to read your locations. Try again in a moment (no need to sign in again).",
            "O acesso foi concluído, mas não foi possível falar com a SmartThings para ler os seus locais. Tente de novo daqui a pouco (sem precisar entrar de novo)."),
        []);

    private FlowStep Done(FlowContext context, SmartThingsLocation location)
    {
        // SmartThings gives a location authorized again with the same app the same single-use tokens: a second
        // integration of it would spend the running one's refresh token.
        if (_mode == SmartThingsAuthMode.OAuth && locations.IsClaimedByOther(_app!.ClientId, location.Id, context.Existing?.Id))
        {
            return new AbortStep("already_configured", new LocalizedText(
                "This SmartThings location is already connected with this app in another integration. Use Sign in again on that integration instead.",
                "Este local da SmartThings já está conectado com este app em outra integração. Use a opção Entrar novamente naquela integração."));
        }

        var config = new JsonObject
        {
            [SmartThingsSettings.LocationIdField] = location.Id,
            [SmartThingsSettings.LocationNameField] = Limit(location.Name, 128),
        };
        if (context.Existing?.Config is { ValueKind: JsonValueKind.Object } previous && previous.Num(SmartThingsSettings.PollIntervalField) is { } poll)
        {
            config[SmartThingsSettings.PollIntervalField] = poll;
        }

        Dictionary<string, string> secrets;
        if (_mode == SmartThingsAuthMode.PersonalAccessToken)
        {
            config[SmartThingsSettings.ModeField] = SmartThingsSettings.PatMode;
            config[SmartThingsSettings.PatIssuedAtField] = context.Time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
            secrets = new Dictionary<string, string>(StringComparer.Ordinal) { [SecretKeys.PersonalAccessToken] = _pat! };
        }
        else
        {
            config[SmartThingsSettings.ModeField] = SmartThingsSettings.OAuthMode;
            config[SmartThingsSettings.ClientIdField] = _app!.ClientId;
            config[SmartThingsSettings.RedirectUriField] = _redirectUri;
            config[SmartThingsSettings.EndpointsField] = _preset;
            if (_preset == SmartThingsSettings.CustomPreset)
            {
                config[SmartThingsSettings.AuthorizeUrlField] = _customAuthorizeUrl;
                config[SmartThingsSettings.TokenUrlField] = _customTokenUrl;
            }

            secrets = new Dictionary<string, string>(_tokens!.ToSecrets(), StringComparer.Ordinal) { [SecretKeys.ClientSecret] = _app.ClientSecret };
        }

        var name = context.Existing?.Name ?? Limit($"SmartThings ({location.Name})", 128);
        return new DoneStep(name, JsonSerializer.SerializeToElement(config)) { Secrets = secrets };
    }

    private static AbortStep PlanRequired() => new("plan_required", new LocalizedText(
        "SmartThings asks for a paid plan (the Personal Plan) for API access. Subscribe in your Samsung account and try again.",
        "A SmartThings pede um plano pago (o Personal Plan) para o acesso pela API. Assine na sua conta Samsung e tente de novo."));

    private JsonElement AppValues()
    {
        var values = new JsonObject
        {
            [SmartThingsSettings.ClientIdField] = _app?.ClientId,
            [SmartThingsSettings.RedirectUriField] = _redirectUri,
            [SmartThingsSettings.EndpointsField] = _preset,
        };
        if (_preset == SmartThingsSettings.CustomPreset)
        {
            values[SmartThingsSettings.AuthorizeUrlField] = _customAuthorizeUrl;
            values[SmartThingsSettings.TokenUrlField] = _customTokenUrl;
        }

        return JsonSerializer.SerializeToElement(values);
    }

    private HttpClient Http() => _http ??= http();

    private static LocalizedText FieldError(ValueErrorKind kind) => kind switch
    {
        ValueErrorKind.Required => new LocalizedText("Required.", "Obrigatório."),
        ValueErrorKind.Unknown => new LocalizedText("Unknown field.", "Campo desconhecido."),
        _ => new LocalizedText("This value is not valid.", "Este valor não é válido."),
    };

    private static JsonElement Without(JsonElement values, string field)
    {
        var copy = values.ValueKind == JsonValueKind.Object ? JsonNode.Parse(values.GetRawText()) as JsonObject ?? [] : [];
        copy.Remove(field);
        return JsonSerializer.SerializeToElement(copy);
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];

    [LoggerMessage(Level = LogLevel.Warning, Message = "SmartThings setup: a pasted redirect did not match this sign-in's state (state missing: {Missing})")]
    private static partial void LogStateMismatch(ILogger logger, bool missing);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SmartThings setup: the code exchange failed ({Kind}, {VendorCode})")]
    private static partial void LogExchangeFailed(ILogger logger, ConnectorErrorKind kind, string? vendorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SmartThings setup: listing locations failed ({Kind})")]
    private static partial void LogLocationsFailed(ILogger logger, ConnectorErrorKind kind);
}

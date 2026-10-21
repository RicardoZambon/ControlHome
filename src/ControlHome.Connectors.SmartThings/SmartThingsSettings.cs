using System.Globalization;
using System.Text.Json;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.SmartThings.Auth;

namespace ControlHome.Connectors.SmartThings;

/// <summary>How the integration signs in.</summary>
internal enum SmartThingsAuthMode
{
    /// <summary>The household's own OAuth-In app (recommended; tokens rotate).</summary>
    OAuth,

    /// <summary>A personal access token: development only (valid 24 h).</summary>
    PersonalAccessToken,
}

/// <summary>
/// A SmartThings integration's parsed (non-secret) configuration — one location per integration. The client secret,
/// the personal access token and the OAuth tokens are secrets: they never appear here.
/// </summary>
/// <param name="EndpointsPreset"><c>documented</c>, <c>homeAssistant</c> or <c>custom</c>.</param>
/// <param name="Endpoints">The resolved OAuth endpoints (OAuth mode), or <c>null</c>.</param>
/// <param name="PatIssuedAtUtc">When the personal access token was entered (development mode: it expires 24 h after it was created).</param>
internal sealed record SmartThingsSettings(
    SmartThingsAuthMode Mode,
    string? ClientId,
    string? RedirectUri,
    string EndpointsPreset,
    OAuthEndpoints? Endpoints,
    string LocationId,
    string? LocationName,
    TimeSpan PollInterval,
    DateTimeOffset? PatIssuedAtUtc)
{
    public const string ModeField = "mode";
    public const string ClientIdField = "clientId";
    public const string ClientSecretField = SecretKeys.ClientSecret;
    public const string RedirectUriField = "redirectUri";
    public const string EndpointsField = "endpoints";
    public const string AuthorizeUrlField = "authorizeUrl";
    public const string TokenUrlField = "tokenUrl";
    public const string PersonalAccessTokenField = SecretKeys.PersonalAccessToken;
    public const string LocationIdField = "locationId";
    public const string LocationNameField = "locationName";
    public const string PollIntervalField = "pollIntervalSeconds";
    public const string PatIssuedAtField = "patIssuedAtUtc";

    public const string OAuthMode = "oauth";
    public const string PatMode = "pat";
    public const string DocumentedPreset = "documented";
    public const string HomeAssistantPreset = "homeAssistant";
    public const string CustomPreset = "custom";

    public const int MinPollSeconds = 30;
    public const int MaxPollSeconds = 120;

    /// <summary>Parses and validates a configuration object (secret members, if present, are ignored).</summary>
    /// <exception cref="InvalidSmartThingsSettingsException">The configuration is not valid.</exception>
    public static SmartThingsSettings Parse(JsonElement config, SmartThingsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var validation = FieldSchemaValidator.Validate(SmartThingsConnectorDescriptor.Instance.ConfigFields, config);
        if (!validation.IsValid)
        {
            var problems = string.Join(", ", validation.Errors.Select(e => $"{e.Argument ?? "config"}: {e.Kind}"));
            throw new InvalidSmartThingsSettingsException(validation.Errors[0].Argument ?? ModeField, $"Invalid SmartThings configuration ({problems}).");
        }

        var mode = config.Str(ModeField) == PatMode ? SmartThingsAuthMode.PersonalAccessToken : SmartThingsAuthMode.OAuth;
        var locationId = config.Str(LocationIdField)?.Trim();
        if (string.IsNullOrEmpty(locationId))
        {
            throw new InvalidSmartThingsSettingsException(LocationIdField, "Invalid SmartThings configuration (locationId is required).");
        }

        var poll = config.Num(PollIntervalField) is { } seconds
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, MinPollSeconds, MaxPollSeconds))
            : options.DefaultPollInterval;
        var issued = config.Str(PatIssuedAtField) is { } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
                ? at
                : (DateTimeOffset?)null;
        var preset = config.Str(EndpointsField) ?? DocumentedPreset;
        if (mode == SmartThingsAuthMode.PersonalAccessToken)
        {
            return new SmartThingsSettings(mode, null, null, preset, null, locationId, config.Str(LocationNameField), poll, issued);
        }

        var clientId = config.Str(ClientIdField)?.Trim();
        if (!IsClientId(clientId))
        {
            throw new InvalidSmartThingsSettingsException(ClientIdField, "Invalid SmartThings configuration (clientId).");
        }

        var redirect = config.Str(RedirectUriField)?.Trim();
        if (!IsRedirectUri(redirect))
        {
            throw new InvalidSmartThingsSettingsException(RedirectUriField, "Invalid SmartThings configuration (redirectUri must be an https URL).");
        }

        var endpoints = ResolveEndpoints(preset, config.Str(AuthorizeUrlField), config.Str(TokenUrlField), options, out var field)
            ?? throw new InvalidSmartThingsSettingsException(field ?? EndpointsField, "Invalid SmartThings configuration (OAuth URLs).");
        return new SmartThingsSettings(mode, clientId, redirect, preset, endpoints, locationId, config.Str(LocationNameField), poll, issued);
    }

    /// <summary>The OAuth endpoints of a preset (custom URLs must be HTTPS on a SmartThings host), or <c>null</c>.</summary>
    /// <param name="invalidField">The field to blame when the result is <c>null</c>.</param>
    public static OAuthEndpoints? ResolveEndpoints(string preset, string? authorizeUrl, string? tokenUrl, SmartThingsOptions options, out string? invalidField)
    {
        ArgumentNullException.ThrowIfNull(options);
        invalidField = null;
        switch (preset)
        {
            case DocumentedPreset:
                return options.DocumentedEndpoints;
            case HomeAssistantPreset:
                return options.HomeAssistantEndpoints;
            case CustomPreset:
                if (!Uri.TryCreate(authorizeUrl?.Trim(), UriKind.Absolute, out var authorize) || !options.IsAllowedOAuthUrl(authorize))
                {
                    invalidField = AuthorizeUrlField;
                    return null;
                }

                if (!Uri.TryCreate(tokenUrl?.Trim(), UriKind.Absolute, out var token) || !options.IsAllowedOAuthUrl(token) || !string.IsNullOrEmpty(token.Query))
                {
                    invalidField = TokenUrlField;
                    return null;
                }

                return new OAuthEndpoints(authorize, token);
            default:
                invalidField = EndpointsField;
                return null;
        }
    }

    public static bool IsClientId(string? value) =>
        value is { Length: > 0 and <= 128 } && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == ':');

    /// <summary>An absolute HTTPS URL without credentials or fragment (SmartThings must redirect the browser to it).</summary>
    public static bool IsRedirectUri(string? value) =>
        value is { Length: <= 2048 }
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment);
}

/// <summary>An invalid SmartThings configuration; <see cref="Field"/> names the offending config field.</summary>
public sealed class InvalidSmartThingsSettingsException(string field, string message) : ArgumentException(message, "config")
{
    public string Field { get; } = field;
}

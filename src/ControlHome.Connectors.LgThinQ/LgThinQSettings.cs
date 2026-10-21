using System.Text.Json;
using ControlHome.Connectors.Abstractions;

namespace ControlHome.Connectors.LgThinQ;

/// <summary>
/// An LG ThinQ integration's parsed (non-secret) configuration. The personal access token, the persistent client id and the
/// MQTT client certificate live in the integration's secret store (<see cref="LgSecrets"/>), never here.
/// </summary>
/// <param name="Country">ISO 3166 code of the LG account's country (not where the user is now).</param>
/// <param name="ApiKey">The <c>x-api-key</c> header: the public constant of LG's SDK unless configured.</param>
/// <param name="TimeZone">The zone of the account's energy days (local midnights), from config or the country.</param>
/// <param name="EnergyEnabled">Fetch daily and monthly energy every night.</param>
internal sealed record LgThinQSettings(string Country, string ApiKey, TimeZoneInfo TimeZone, bool EnergyEnabled)
{
    public const string PatField = "pat";
    public const string CountryField = "country";
    public const string ApiKeyField = "apiKey";
    public const string TimeZoneField = "timeZone";
    public const string EnergyField = "energy";

    /// <summary>
    /// The public <c>x-api-key</c> constant of LG's official SDKs (pythinqconnect <c>const.py</c> <c>API_KEY</c>,
    /// jsthinqconnect <c>Const.ts</c>): the same for every PAT user, not a credential (each household's PAT is). Kept
    /// configurable (<see cref="ApiKeyField"/>) in case LG rotates it.
    /// </summary>
    public const string DefaultApiKey = "v6GFvkweNo7DK7yD3ylIZ9w52aKBU0eJ7wLXkSR3"; // gitleaks:allow (public SDK constant, see NOTICE)

    /// <summary>The API region (<c>aic</c> for Brazil and the Americas).</summary>
    public string Region => LgCountries.RegionOf(Country)!;

    /// <summary>The REST base URL (<c>https://api-{region}.lgthinq.com/</c>).</summary>
    public Uri BaseUrl => LgCountries.BaseUrlOf(Country);

    /// <summary>Parses and validates a configuration object (secret members, if present, are ignored).</summary>
    /// <param name="localZone">The zone to fall back on when neither the config nor the country names one.</param>
    /// <exception cref="InvalidLgThinQSettingsException">The configuration does not match the descriptor's config fields.</exception>
    public static LgThinQSettings Parse(JsonElement config, TimeZoneInfo localZone)
    {
        ArgumentNullException.ThrowIfNull(localZone);
        var fields = LgThinQConnectorDescriptor.Instance.ConfigFields.Where(f => !f.Secret).ToList();
        var values = WithoutSecrets(config);
        var validation = FieldSchemaValidator.Validate(fields, values);
        if (!validation.IsValid)
        {
            var problems = string.Join(", ", validation.Errors.Select(e => $"{e.Argument ?? "config"}: {e.Kind}"));
            throw new InvalidLgThinQSettingsException(validation.Errors[0].Argument ?? CountryField, $"Invalid LG ThinQ configuration ({problems}).");
        }

        var country = (Text(values, CountryField) ?? LgCountries.Default).ToUpperInvariant();
        if (LgCountries.RegionOf(country) is null)
        {
            throw new InvalidLgThinQSettingsException(CountryField, $"Invalid LG ThinQ configuration (country: '{country}' is not supported).");
        }

        TimeZoneInfo zone;
        if (Text(values, TimeZoneField) is { Length: > 0 } zoneId)
        {
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(zoneId.Trim(), out var found))
            {
                throw new InvalidLgThinQSettingsException(TimeZoneField, "Invalid LG ThinQ configuration (timeZone: unknown time zone).");
            }

            zone = found;
        }
        else
        {
            zone = LgCountries.DefaultTimeZone(country, localZone);
        }

        var apiKey = Text(values, ApiKeyField)?.Trim();
        if (apiKey is { Length: > 0 } && (apiKey.Length > 256 || apiKey.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))))
        {
            throw new InvalidLgThinQSettingsException(ApiKeyField, "Invalid LG ThinQ configuration (apiKey: not a header value).");
        }

        return new LgThinQSettings(
            country,
            string.IsNullOrEmpty(apiKey) ? DefaultApiKey : apiKey,
            zone,
            values.ValueKind == JsonValueKind.Object && values.TryGetProperty(EnergyField, out var energy) && energy.ValueKind == JsonValueKind.False
                ? false
                : true);
    }

    /// <summary>Parses, or <c>null</c> when the configuration is invalid.</summary>
    public static LgThinQSettings? TryParse(JsonElement config, TimeZoneInfo localZone)
    {
        try
        {
            return Parse(config, localZone);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The configuration without the secret members (the PAT never goes to the config).</summary>
    public static JsonElement WithoutSecrets(JsonElement config)
    {
        var copy = config.ValueKind == JsonValueKind.Object
            ? System.Text.Json.Nodes.JsonNode.Parse(config.GetRawText()) as System.Text.Json.Nodes.JsonObject ?? []
            : [];
        foreach (var field in LgThinQConnectorDescriptor.Instance.ConfigFields.Where(f => f.Secret))
        {
            copy.Remove(field.Key);
        }

        copy.Remove(LgThinQSetupFlow.NameField);
        return JsonSerializer.SerializeToElement(copy);
    }

    private static string? Text(JsonElement config, string field) =>
        config.ValueKind == JsonValueKind.Object && config.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>An invalid LG ThinQ configuration; <see cref="Field"/> names the offending config field.</summary>
internal sealed class InvalidLgThinQSettingsException(string field, string message) : ArgumentException(message, "config")
{
    public string Field { get; } = field;
}

/// <summary>The keys of the integration's secrets (all integration-level; nothing per device).</summary>
internal static class LgSecrets
{
    /// <summary>The personal access token (a config field marked secret).</summary>
    public const string Pat = LgThinQSettings.PatField;

    /// <summary>
    /// The persistent client id (<c>controlhome-&lt;guid&gt;</c>): created once per integration, kept across restarts and
    /// token replacements (LG warns that creating many clients can get API calls blocked).
    /// </summary>
    public const string ClientId = "clientId";

    /// <summary>The MQTT client key, certificate and topic as one JSON value (<see cref="Mqtt.LgMqttCredentials"/>): saved atomically.</summary>
    public const string MqttCredentials = "mqttCredentials";
}

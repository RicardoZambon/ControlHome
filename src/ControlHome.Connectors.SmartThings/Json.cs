using System.Globalization;
using System.Text.Json;

namespace ControlHome.Connectors.SmartThings;

/// <summary>Lenient JSON accessors for vendor payloads: wrong types read as absent, nothing throws.</summary>
internal static class Json
{
    /// <summary>Parses a document; <c>null</c> when it is not JSON.</summary>
    public static JsonElement? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A member that is present and not JSON <c>null</c>.</summary>
    public static JsonElement? Get(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    public static JsonElement? Get(this JsonElement? element, string name) => element is { } value ? value.Get(name) : null;

    public static string? Str(this JsonElement element, string name) =>
        element.Get(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public static string? Str(this JsonElement? element, string name) => element is { } value ? value.Str(name) : null;

    public static double? Num(this JsonElement element, string name) => element.Get(name) is { } value ? Number(value) : null;

    public static long? Long(this JsonElement element, string name) =>
        element.Get(name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var number) ? number : null;

    public static bool? Bool(this JsonElement element, string name) =>
        element.Get(name) is { ValueKind: JsonValueKind.True or JsonValueKind.False } value ? value.GetBoolean() : null;

    public static IEnumerable<JsonElement> Array(this JsonElement element, string name) =>
        element.Get(name) is { ValueKind: JsonValueKind.Array } value ? value.EnumerateArray() : [];

    public static IEnumerable<JsonProperty> Members(this JsonElement element, string name) =>
        element.Get(name) is { ValueKind: JsonValueKind.Object } value ? value.EnumerateObject() : [];

    /// <summary>A finite number from a JSON number or a numeric string.</summary>
    public static double? Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetDouble(out var number) && double.IsFinite(number) => number,
        JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed) => parsed,
        _ => null,
    };

    /// <summary>A JSON value as text: strings as is, numbers invariant, booleans <c>true</c>/<c>false</c>; <c>null</c> otherwise.</summary>
    public static string? AsText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    /// <summary>A boolean from <c>true</c>/<c>false</c> or the strings SmartThings uses for them.</summary>
    public static bool? AsBool(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => value.GetString()?.Trim().ToLowerInvariant() switch
        {
            "true" or "on" or "enabled" or "yes" => true,
            "false" or "off" or "disabled" or "no" => false,
            _ => null,
        },
        _ => null,
    };

    /// <summary>Strings of a JSON array (other items skipped).</summary>
    public static IReadOnlyList<string> Strings(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Array } array
            ? [.. array.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!)]
            : [];

    /// <summary>An ISO 8601 timestamp, or <c>null</c>.</summary>
    public static DateTimeOffset? Time(JsonElement element, string name) =>
        element.Str(name) is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
            ? time
            : null;
}

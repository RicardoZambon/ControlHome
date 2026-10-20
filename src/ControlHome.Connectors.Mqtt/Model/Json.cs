using System.Text.Json;
using System.Text.Json.Nodes;

namespace ControlHome.Connectors.Mqtt.Model;

/// <summary>Lenient JSON accessors for vendor payloads (wrong types read as absent, never throw).</summary>
internal static class Json
{
    /// <summary>Parses a payload; <c>null</c> when it is not JSON.</summary>
    public static JsonElement? TryParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true });
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static JsonElement? Get(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    public static string? Str(this JsonElement element, string name) =>
        element.Get(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    /// <summary>A string, or a number/bool rendered as invariant text.</summary>
    public static string? Text(this JsonElement element, string name) => element.Get(name) is { } value ? AsText(value) : null;

    public static double? Num(this JsonElement element, string name) => element.Get(name) is { } value ? Conversions.Number(value) : null;

    public static int? Int(this JsonElement element, string name) =>
        element.Get(name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) ? number : null;

    public static bool? Bool(this JsonElement element, string name) =>
        element.Get(name) is { ValueKind: JsonValueKind.True or JsonValueKind.False } value ? value.GetBoolean() : null;

    public static IEnumerable<JsonElement> Array(this JsonElement element, string name) =>
        element.Get(name) is { ValueKind: JsonValueKind.Array } value ? value.EnumerateArray() : [];

    /// <summary>A JSON value as text: strings as is, numbers invariant, booleans <c>true</c>/<c>false</c>; <c>null</c> otherwise.</summary>
    public static string? AsText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    /// <summary>A JSON node for a value (numbers stay numbers).</summary>
    public static JsonNode? Node(JsonElement value) => JsonNode.Parse(value.GetRawText());

    public static JsonNode Number(double value) =>
        Math.Abs(value - Math.Round(value)) < 1e-9 && Math.Abs(value) < long.MaxValue
            ? JsonValue.Create((long)Math.Round(value))
            : JsonValue.Create(value);

    public static string Serialize(JsonNode node) => node.ToJsonString();
}

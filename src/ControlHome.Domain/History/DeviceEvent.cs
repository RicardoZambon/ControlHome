using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ControlHome.Domain.History;

/// <summary>
/// A momentary event of an attribute (button press, doorbell ring, camera motion) — table <c>DeviceEvent</c>,
/// append-only, no key: clustered on <c>(TsUtc, AttributeId)</c>; kept <see cref="RetentionSettings.DeviceEventDays"/>.
/// </summary>
public sealed class DeviceEvent
{
    public const int MaxEventTypeLength = 64;

    /// <summary>Payloads longer than this (JSON text) are not stored.</summary>
    public const int MaxPayloadLength = 4000;

    /// <summary>Payloads nested deeper than this are not stored (SQL Server's <c>json</c> type allows 128 levels).</summary>
    public const int MaxPayloadDepth = 64;

    private static readonly JsonWriterOptions _writerOptions = new()
    {
        // The text goes to a json column, never into HTML: keep it compact and readable (é stays é).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public int AttributeId { get; set; }

    /// <summary>When it happened (stored as <c>datetime2(3)</c>, UTC).</summary>
    public DateTimeOffset TsUtc { get; set; }

    /// <summary>The event name, e.g. <c>double</c> for a button's double press.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>The event's details: a JSON object (<c>json</c> column), never secrets.</summary>
    public string? Payload { get; set; }

    /// <summary>
    /// An event's details as stored in <see cref="Payload"/>: canonical JSON text that SQL Server's <c>json</c> type always
    /// accepts — an object or array (a scalar is wrapped as <c>{"value": …}</c>), numbers rewritten as plain decimals,
    /// comments and trailing commas a lenient parser let through dropped. <c>null</c> when absent or not storable: longer
    /// than <see cref="MaxPayloadLength"/>, nested deeper than <see cref="MaxPayloadDepth"/>, or holding a number beyond
    /// <see cref="decimal"/>'s range (SQL Server's <c>json</c> rejects numbers it cannot hold in 38 digits — even
    /// <c>1e37</c> — and one rejected payload would fail the whole write-behind flush on every retry).
    /// </summary>
    public static string? StoredPayload(JsonElement? data)
    {
        if (data is not { } element || element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>();
        try
        {
            using var writer = new Utf8JsonWriter(buffer, _writerOptions);
            var written = element.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? TryWrite(writer, element, 1)
                : TryWriteWrapped(writer, element);
            if (!written)
            {
                return null;
            }

            writer.Flush();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null; // e.g. a string holding invalid UTF-16
        }

        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        return json.Length <= MaxPayloadLength ? json : null;
    }

    /// <summary><see cref="StoredPayload(JsonElement?)"/> of JSON text; <c>null</c> when it is not (strict) JSON.</summary>
    public static string? StoredPayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return StoredPayload(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryWriteWrapped(Utf8JsonWriter writer, JsonElement element)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("value");
        if (!TryWrite(writer, element, 2))
        {
            return false;
        }

        writer.WriteEndObject();
        return true;
    }

    private static bool TryWrite(Utf8JsonWriter writer, JsonElement element, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (depth > MaxPayloadDepth)
                {
                    return false;
                }

                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (!TryWrite(writer, property.Value, depth + 1))
                    {
                        return false;
                    }
                }

                writer.WriteEndObject();
                return true;
            case JsonValueKind.Array:
                if (depth > MaxPayloadDepth)
                {
                    return false;
                }

                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    if (!TryWrite(writer, item, depth + 1))
                    {
                        return false;
                    }
                }

                writer.WriteEndArray();
                return true;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                return true;
            case JsonValueKind.Number:
                // A decimal (at most 29 significant digits, written without an exponent) is always storable.
                if (!element.TryGetDecimal(out var number))
                {
                    return false;
                }

                writer.WriteNumberValue(number);
                return true;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                return true;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                return true;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                return true;
            default:
                return false;
        }
    }
}

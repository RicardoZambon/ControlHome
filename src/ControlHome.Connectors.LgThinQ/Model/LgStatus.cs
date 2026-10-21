using System.Text.Json;
using System.Text.Json.Nodes;

namespace ControlHome.Connectors.LgThinQ.Model;

/// <summary>
/// A device's last known status: a full <c>GET devices/{id}/state</c> replaces it, an MQTT <c>DEVICE_STATUS</c> report is
/// merged into it — newest wins, by arrival (LG's reports can arrive out of order and carry no usable sequence). Objects
/// merge member by member; lists of compartments or locations merge entry by entry (matched on <c>locationName</c>,
/// <c>location.locationName</c> or <c>unit</c>); anything else is replaced. Thread-safe.
/// </summary>
internal sealed class LgStatus
{
    private readonly Lock _lock = new();
    private JsonNode? _status;

    public bool IsKnown
    {
        get
        {
            lock (_lock)
            {
                return _status is not null;
            }
        }
    }

    /// <summary>The current status as JSON (an empty object while unknown).</summary>
    public JsonElement Current
    {
        get
        {
            lock (_lock)
            {
                return _status is null ? JsonSerializer.SerializeToElement(new JsonObject()) : JsonSerializer.SerializeToElement(_status);
            }
        }
    }

    /// <summary>Replaces the status with a full one; returns the new status.</summary>
    public JsonElement Replace(JsonElement full)
    {
        lock (_lock)
        {
            _status = JsonNode.Parse(full.GetRawText());
            return JsonSerializer.SerializeToElement(_status);
        }
    }

    /// <summary>Merges a partial report into the status; returns the merged status.</summary>
    public JsonElement Merge(JsonElement report)
    {
        lock (_lock)
        {
            var incoming = JsonNode.Parse(report.GetRawText());
            _status = _status is null ? incoming : Merge(_status, incoming);
            return _status is null ? JsonSerializer.SerializeToElement(new JsonObject()) : JsonSerializer.SerializeToElement(_status);
        }
    }

    /// <summary>The merge of <paramref name="report"/> into <paramref name="status"/> (pure; for tests and the runtime).</summary>
    public static JsonNode? Merge(JsonNode? status, JsonNode? report)
    {
        switch (status, report)
        {
            case (JsonObject target, JsonObject source):
                foreach (var (key, value) in source.ToList())
                {
                    target[key] = target.TryGetPropertyValue(key, out var existing) ? Merge(existing?.DeepClone(), value?.DeepClone()) : value?.DeepClone();
                }

                return target;
            case (JsonArray target, JsonArray source) when Keyed(target) && Keyed(source):
                var result = new JsonArray([.. target.Select(t => t?.DeepClone())]);
                foreach (var entry in source)
                {
                    var key = KeyOf(entry);
                    var index = result.ToList().FindIndex(t => KeyOf(t) == key);
                    if (index >= 0)
                    {
                        result[index] = Merge(result[index]?.DeepClone(), entry?.DeepClone());
                    }
                    else
                    {
                        result.Add(entry?.DeepClone());
                    }
                }

                return result;
            default:
                return report?.DeepClone();
        }
    }

    private static bool Keyed(JsonArray array) => array.Count > 0 && array.All(e => KeyOf(e) is not null);

    private static string? KeyOf(JsonNode? entry) =>
        entry is JsonObject item
            ? Text(item, "locationName") is { } location ? "l:" + location
            : item["location"] is JsonObject place && Text(place, "locationName") is { } nested ? "l:" + nested
            : Text(item, "unit") is { } unit ? "u:" + unit
            : null
            : null;

    private static string? Text(JsonObject item, string name) =>
        item.TryGetPropertyValue(name, out var value) && value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;
}

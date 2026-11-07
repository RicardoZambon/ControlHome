using System.Text.Json;
using System.Text.Json.Nodes;

namespace ControlHome.Connectors.LgThinQ.Model;

/// <summary>
/// A device's last known status: a full <c>GET devices/{id}/state</c> replaces it, an MQTT <c>DEVICE_STATUS</c> report is
/// merged into it — newest wins, by arrival (LG's reports can arrive out of order and carry no usable sequence). A full
/// state counts as arriving when it was asked for: reports merged while the request was on its way are newer and are
/// merged again on top of it (<see cref="Replace(JsonElement, long?)"/>). Objects merge member by member; lists of
/// compartments or locations merge entry by entry (matched on <c>locationName</c>, <c>location.locationName</c> or
/// <c>unit</c>); anything else is replaced. Thread-safe.
/// </summary>
internal sealed class LgStatus
{
    /// <summary>Reports kept to re-apply over a full state that was asked for before them (a poll takes a second or two).</summary>
    private const int RecentReports = 64;

    private readonly Lock _lock = new();
    private readonly Queue<(long Version, JsonNode? Report)> _recent = new();
    private JsonNode? _status;
    private long _version;

    /// <summary>The newest report version no longer kept in <see cref="_recent"/>.</summary>
    private long _dropped;

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

    /// <summary>Counts every change (replace or merge): what a full state request remembers as "asked at".</summary>
    public long Version
    {
        get
        {
            lock (_lock)
            {
                return _version;
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

    /// <summary>
    /// Replaces the status with a full one; returns the new status. With <paramref name="askedAt"/> (the
    /// <see cref="Version"/> when the full state was requested), reports merged since then are merged again on top of it.
    /// </summary>
    public JsonElement Replace(JsonElement full, long? askedAt = null)
    {
        lock (_lock)
        {
            var current = _status;
            var replaced = JsonNode.Parse(full.GetRawText());
            if (askedAt is { } asked && asked < _version)
            {
                if (_dropped <= asked)
                {
                    foreach (var (_, report) in _recent.Where(r => r.Version > asked))
                    {
                        replaced = replaced is null ? report?.DeepClone() : Merge(replaced, report?.DeepClone());
                    }
                }
                else
                {
                    // More reports came meanwhile than are kept: what is known now wins over the older answer.
                    replaced = replaced is null ? current?.DeepClone() : Merge(replaced, current?.DeepClone());
                }
            }

            _status = replaced;
            _version++;
            return _status is null ? JsonSerializer.SerializeToElement(new JsonObject()) : JsonSerializer.SerializeToElement(_status);
        }
    }

    /// <summary>Merges a partial report into the status; returns the merged status.</summary>
    public JsonElement Merge(JsonElement report)
    {
        lock (_lock)
        {
            var incoming = JsonNode.Parse(report.GetRawText());
            _status = _status is null ? incoming?.DeepClone() : Merge(_status, incoming?.DeepClone());
            _version++;
            _recent.Enqueue((_version, incoming));
            while (_recent.Count > RecentReports)
            {
                _dropped = _recent.Dequeue().Version;
            }

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

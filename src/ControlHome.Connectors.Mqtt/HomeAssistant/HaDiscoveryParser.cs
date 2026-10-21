using System.Text.Json;
using System.Text.Json.Nodes;
using ControlHome.Connectors.Mqtt.Model;

namespace ControlHome.Connectors.Mqtt.HomeAssistant;

/// <summary>Where a discovery config was published.</summary>
/// <param name="Component">The platform (<c>sensor</c>, <c>light</c>, …), or <c>device</c> for device-based discovery.</param>
/// <param name="NodeId">The optional node id level of single-component topics.</param>
internal sealed record HaConfigTopic(string Topic, string Component, string? NodeId, string ObjectId)
{
    public bool IsDeviceBased => Component == "device";
}

/// <summary>One entity of a discovery config, with abbreviations and <c>~</c> expanded.</summary>
/// <param name="Key">Stable within the device: the unique id, else the object (component) id.</param>
internal sealed record HaEntity(string ConfigTopic, string Component, string ObjectId, string Key, JsonElement Config)
{
    public string? Name => Config.Str("name");
}

/// <summary>A parsed discovery config: the device it belongs to and its entities (one, or several for device-based).</summary>
/// <param name="DeviceKey">The external id of the device (<c>ha:&lt;identifier&gt;</c>).</param>
/// <param name="FromZigbee2Mqtt">Published by Zigbee2MQTT's own Home Assistant integration.</param>
internal sealed record HaDiscoveryMessage(
    HaConfigTopic Topic,
    string DeviceKey,
    JsonElement? Device,
    IReadOnlyList<HaEntity> Entities,
    bool FromZigbee2Mqtt);

/// <summary>
/// Parses Home Assistant MQTT discovery: device-based configs (<c>&lt;prefix&gt;/device/&lt;id&gt;/config</c> with
/// <c>dev</c>, <c>o</c> and a <c>cmps</c> map whose entries carry <c>p</c>) and single-component configs
/// (<c>&lt;prefix&gt;/&lt;component&gt;/[&lt;node_id&gt;/]&lt;object_id&gt;/config</c>). Expands abbreviations (also inside
/// <c>dev</c>, <c>o</c> and <c>availability</c>) and the <c>~</c> base topic (keys ending in <c>topic</c>).
/// </summary>
internal static class HaDiscoveryParser
{
    public const string ExternalIdPrefix = "ha:";

    private const string Base = "~";
    private static readonly HashSet<string> _deviceLevelKeys = new(StringComparer.Ordinal) { "device", "origin", "components" };

    /// <summary>The config topic's parts, or <c>null</c> when <paramref name="topic"/> is not a discovery config topic.</summary>
    public static HaConfigTopic? ParseTopic(string prefix, string topic)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(topic);
        if (!topic.StartsWith(prefix + "/", StringComparison.Ordinal) || !topic.EndsWith("/config", StringComparison.Ordinal))
        {
            return null;
        }

        var levels = topic[(prefix.Length + 1)..^"/config".Length].Split('/');
        return levels switch
        {
            [var component, var objectId] when Valid(component) && Valid(objectId) => new HaConfigTopic(topic, component, null, objectId),
            [var component, var node, var objectId] when component != "device" && Valid(component) && Valid(node) && Valid(objectId) =>
                new HaConfigTopic(topic, component, node, objectId),
            _ => null,
        };

        static bool Valid(string level) => level.Length > 0;
    }

    /// <summary>Parses a (non-empty) config payload; <c>null</c> when it is not a JSON object or has no entity.</summary>
    public static HaDiscoveryMessage? Parse(HaConfigTopic topic, string payload)
    {
        ArgumentNullException.ThrowIfNull(topic);
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(payload) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is null)
        {
            return null;
        }

        var config = Expand(root, HaAbbreviations.Config);
        var device = config["device"] is JsonObject deviceNode ? Expand(deviceNode, HaAbbreviations.Device) : null;
        var origin = config["origin"] is JsonObject originNode ? Expand(originNode, HaAbbreviations.Origin) : null;
        var entities = new List<HaEntity>();
        if (topic.IsDeviceBased)
        {
            if (config["components"] is not JsonObject components)
            {
                return null;
            }

            var shared = config.Where(p => !_deviceLevelKeys.Contains(p.Key)).ToList();
            foreach (var (id, node) in components)
            {
                if (node is not JsonObject componentNode)
                {
                    continue;
                }

                var component = Expand(componentNode, HaAbbreviations.Config);
                if (component["platform"]?.GetValueKind() != JsonValueKind.String)
                {
                    continue;
                }

                // A component with only its platform is being removed from the device.
                if (component.Count == 1)
                {
                    continue;
                }

                foreach (var (key, value) in shared)
                {
                    if (!component.ContainsKey(key))
                    {
                        component[key] = value?.DeepClone();
                    }
                }

                ExpandBaseTopic(component);
                var element = JsonSerializer.SerializeToElement(component);
                entities.Add(new HaEntity(topic.Topic, component["platform"]!.GetValue<string>(), id, element.Str("unique_id") ?? id, element));
            }
        }
        else
        {
            var entity = new JsonObject(config.Where(p => p.Key is not ("device" or "origin")).Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
            ExpandBaseTopic(entity);
            var element = JsonSerializer.SerializeToElement(entity);
            var objectId = topic.NodeId is null ? topic.ObjectId : $"{topic.NodeId}_{topic.ObjectId}";
            entities.Add(new HaEntity(topic.Topic, topic.Component, topic.ObjectId, element.Str("unique_id") ?? objectId, element));
        }

        var deviceElement = device is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(device);
        var fallback = topic.IsDeviceBased
            ? topic.ObjectId
            : entities[0].Config.Str("unique_id") ?? $"{topic.Component}_{(topic.NodeId is null ? string.Empty : topic.NodeId + "_")}{topic.ObjectId}";
        return new HaDiscoveryMessage(
            topic,
            DeviceKey(deviceElement, fallback),
            deviceElement,
            entities,
            IsZigbee2Mqtt(deviceElement, origin));
    }

    /// <summary><c>ha:</c> + the device's first identifier, else its first connection, else <paramref name="fallback"/>.</summary>
    public static string DeviceKey(JsonElement? device, string fallback)
    {
        if (device is { } info)
        {
            var identifiers = info.Get("identifiers");
            var identifier = identifiers switch
            {
                { ValueKind: JsonValueKind.String } single => single.GetString(),
                { ValueKind: JsonValueKind.Array } list => list.EnumerateArray().Select(Json.AsText).FirstOrDefault(i => !string.IsNullOrWhiteSpace(i)),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(identifier))
            {
                return ExternalIdPrefix + identifier;
            }

            foreach (var connection in info.Array("connections"))
            {
                if (connection.ValueKind == JsonValueKind.Array && connection.GetArrayLength() == 2
                    && Json.AsText(connection[0]) is { } type && Json.AsText(connection[1]) is { Length: > 0 } value)
                {
                    return $"{ExternalIdPrefix}{type}_{value}";
                }
            }
        }

        return ExternalIdPrefix + fallback;
    }

    private static bool IsZigbee2Mqtt(JsonElement? device, JsonObject? origin) =>
        string.Equals(origin?["name"]?.GetValueKind() == JsonValueKind.String ? origin["name"]!.GetValue<string>() : null, "Zigbee2MQTT", StringComparison.OrdinalIgnoreCase)
        || DeviceKey(device, string.Empty).StartsWith(ExternalIdPrefix + "zigbee2mqtt_", StringComparison.Ordinal);

    /// <summary>A copy of <paramref name="node"/> with abbreviated keys expanded (and nested blocks expanded too).</summary>
    private static JsonObject Expand(JsonObject node, IReadOnlyDictionary<string, string> abbreviations)
    {
        var result = new JsonObject();
        foreach (var (key, value) in node)
        {
            var name = abbreviations.GetValueOrDefault(key, key);
            var copy = value?.DeepClone();
            if (name == "availability" && copy is JsonArray availability)
            {
                for (var i = 0; i < availability.Count; i++)
                {
                    if (availability[i] is JsonObject entry)
                    {
                        availability[i] = Expand(entry, HaAbbreviations.Config);
                    }
                }
            }

            // An explicit full name wins over its abbreviation if both are present.
            if (!result.ContainsKey(name) || name == key)
            {
                result[name] = copy;
            }
        }

        return result;
    }

    /// <summary>Applies the <c>~</c> base topic to every <c>*topic</c> value starting or ending with <c>~</c>.</summary>
    private static void ExpandBaseTopic(JsonObject config)
    {
        if (config[Base]?.GetValueKind() != JsonValueKind.String)
        {
            return;
        }

        var baseTopic = config[Base]!.GetValue<string>();
        config.Remove(Base);
        foreach (var (key, value) in config.ToList())
        {
            if (key.EndsWith("topic", StringComparison.Ordinal) && value?.GetValueKind() == JsonValueKind.String)
            {
                config[key] = Apply(value.GetValue<string>());
            }
        }

        if (config["availability"] is JsonArray availability)
        {
            foreach (var entry in availability.OfType<JsonObject>())
            {
                if (entry["topic"]?.GetValueKind() == JsonValueKind.String)
                {
                    entry["topic"] = Apply(entry["topic"]!.GetValue<string>());
                }
            }
        }

        string Apply(string topic)
        {
            if (topic.StartsWith(Base, StringComparison.Ordinal))
            {
                topic = baseTopic + topic[1..];
            }

            if (topic.EndsWith(Base, StringComparison.Ordinal))
            {
                topic = topic[..^1] + baseTopic;
            }

            return topic;
        }
    }
}

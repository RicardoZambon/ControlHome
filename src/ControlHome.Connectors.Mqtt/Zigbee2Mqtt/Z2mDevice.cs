using System.Text.Json;
using System.Text.Json.Nodes;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.Mqtt.Model;
using ControlHome.Connectors.Mqtt.Transport;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;

namespace ControlHome.Connectors.Mqtt.Zigbee2Mqtt;

/// <summary>One property of a Zigbee2MQTT state message feeding one attribute.</summary>
internal sealed record Z2mStateBinding(string Property, string Endpoint, string Capability, string Attribute, Func<JsonElement, StateValue?> Convert);

/// <summary>The <c>action</c> property: each value becomes a momentary event (or nothing).</summary>
internal sealed record Z2mActionBinding(string Property, string Endpoint, Func<string, (string Capability, string Attribute, string Event)?> Map);

/// <summary>Builds the payload of one command: the topic suffix (<c>set</c> or a bridge request) and a JSON object.</summary>
internal delegate (string Topic, JsonObject Payload)? Z2mCommandBuilder(ValidatedCommand command);

/// <summary>
/// A Zigbee2MQTT device (or the bridge itself): state from <c>&lt;base&gt;/&lt;friendly_name&gt;</c> (a JSON object),
/// availability from <c>&lt;base&gt;/&lt;friendly_name&gt;/availability</c> (<c>{"state":"online"}</c> or plain text), commands
/// as JSON to <c>&lt;base&gt;/&lt;friendly_name&gt;/set</c> (one merged object per batch).
/// </summary>
internal sealed class Z2mDevice : MqttDevice
{
    private readonly IReadOnlyList<Z2mStateBinding> _states;
    private readonly IReadOnlyList<Z2mActionBinding> _actions;
    private readonly IReadOnlyDictionary<(string Endpoint, string Capability, string Command), Z2mCommandBuilder> _commands;
    private readonly IReadOnlyList<string> _gettable;
    private readonly string _stateTopic;
    private readonly string _availabilityTopic;

    public Z2mDevice(
        DiscoveredDevice discovered,
        string baseTopic,
        string friendlyName,
        bool isBridge,
        IReadOnlyList<Z2mStateBinding> states,
        IReadOnlyList<Z2mActionBinding> actions,
        IReadOnlyDictionary<(string Endpoint, string Capability, string Command), Z2mCommandBuilder> commands,
        IReadOnlyList<string> gettable)
        : base(discovered)
    {
        BaseTopic = baseTopic;
        FriendlyName = friendlyName;
        IsBridge = isBridge;
        _states = states;
        _actions = actions;
        _commands = commands;
        _gettable = gettable;
        _stateTopic = isBridge ? $"{baseTopic}/bridge/info" : $"{baseTopic}/{friendlyName}";
        _availabilityTopic = isBridge ? $"{baseTopic}/bridge/state" : $"{baseTopic}/{friendlyName}/availability";
        Topics = [_stateTopic, _availabilityTopic];
    }

    public string BaseTopic { get; }

    public string FriendlyName { get; }

    /// <summary>The coordinator (parent of every other device of the base topic).</summary>
    public bool IsBridge { get; }

    public override IReadOnlyCollection<string> Topics { get; }

    /// <summary>
    /// Zigbee2MQTT publishes availability only when its availability feature is on; without it the device counts as
    /// online once it reports.
    /// </summary>
    public override bool HasAvailabilityTopic => false;

    public override IReadOnlyList<MqttReading> Read(MqttMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (string.Equals(message.Topic, _availabilityTopic, StringComparison.Ordinal))
        {
            return ParseAvailability(message.Text) is { } availability ? [new MqttReading.Reachability(availability)] : [];
        }

        if (!string.Equals(message.Topic, _stateTopic, StringComparison.Ordinal) || Json.TryParse(message.Text) is not { ValueKind: JsonValueKind.Object } state)
        {
            return [];
        }

        return ReadState(state);
    }

    /// <summary>The readings in one state object (pure: used by the mapper tests).</summary>
    public IReadOnlyList<MqttReading> ReadState(JsonElement state)
    {
        var readings = new List<MqttReading>();
        foreach (var binding in _states)
        {
            if (state.TryGetProperty(binding.Property, out var raw) && raw.ValueKind != JsonValueKind.Null && binding.Convert(raw) is { } value)
            {
                readings.Add(new MqttReading.State(binding.Endpoint, binding.Capability, binding.Attribute, value));
            }
        }

        foreach (var action in _actions)
        {
            if (state.Str(action.Property) is { Length: > 0 } name && action.Map(name) is { } mapped)
            {
                readings.Add(new MqttReading.Event(action.Endpoint, mapped.Capability, mapped.Attribute, mapped.Event));
            }
        }

        return readings;
    }

    public override IReadOnlyList<MqttPublish> Refresh()
    {
        if (_gettable.Count == 0)
        {
            return [];
        }

        var payload = new JsonObject();
        foreach (var property in _gettable)
        {
            payload[property] = string.Empty;
        }

        return [new MqttPublish($"{BaseTopic}/{FriendlyName}/get", Json.Serialize(payload))];
    }

    protected override CommandPlan? PlanCommands(IReadOnlyList<ValidatedCommand> commands)
    {
        // Commands of one batch to the same topic are merged into one JSON object (Zigbee2MQTT applies them together).
        var merged = new List<(string Topic, JsonObject Payload)>();
        foreach (var command in commands)
        {
            if (!_commands.TryGetValue((command.Endpoint, command.Capability, command.Name), out var build) || build(command) is not { } built)
            {
                return null;
            }

            var target = merged.FirstOrDefault(m => string.Equals(m.Topic, built.Topic, StringComparison.Ordinal));
            if (target.Payload is null)
            {
                merged.Add(built);
                continue;
            }

            foreach (var (key, value) in built.Payload.ToList())
            {
                built.Payload.Remove(key);
                target.Payload[key] = value;
            }
        }

        return new CommandPlan([.. merged.Select(m => new MqttPublish(m.Topic, Json.Serialize(m.Payload)))], []);
    }

    /// <summary><c>{"state":"online"}</c> (Zigbee2MQTT ≥ 1.22) or plain <c>online</c>/<c>offline</c>.</summary>
    public static Availability? ParseAvailability(string payload)
    {
        var text = Json.TryParse(payload) is { ValueKind: JsonValueKind.Object } json ? json.Str("state") : payload.Trim();
        return text?.ToLowerInvariant() switch
        {
            "online" => Availability.Online,
            "offline" => Availability.Offline,
            _ => null,
        };
    }
}

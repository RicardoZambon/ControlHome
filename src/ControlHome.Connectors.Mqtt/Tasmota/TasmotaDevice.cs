using System.Globalization;
using System.Text.Json;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.Mqtt.Model;
using ControlHome.Connectors.Mqtt.Transport;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;

namespace ControlHome.Connectors.Mqtt.Tasmota;

/// <summary>A sensor value in Tasmota's <c>SENSOR</c> JSON: <c>{"&lt;Sensor&gt;": {"&lt;Property&gt;": value}}</c>.</summary>
internal sealed record TasmotaSensorBinding(string Sensor, string Property, string Endpoint, string Capability, string Attribute, Func<double, double> Convert);

/// <summary>
/// A Tasmota device from native discovery (<c>tasmota/discovery/&lt;MAC&gt;/config</c> + <c>/sensors</c>). Relays report on
/// <c>stat/&lt;t&gt;/RESULT</c>, <c>stat/&lt;t&gt;/POWER&lt;n&gt;</c> and <c>tele/&lt;t&gt;/STATE</c>; sensors and energy on
/// <c>tele/&lt;t&gt;/SENSOR</c> (and <c>stat/&lt;t&gt;/STATUS10</c>); availability on the LWT <c>tele/&lt;t&gt;/LWT</c>.
/// Commands go to <c>cmnd/&lt;t&gt;/POWER&lt;n&gt;</c> (ON/OFF/TOGGLE) and <c>cmnd/&lt;t&gt;/Dimmer</c>. Topics follow the
/// device's full-topic pattern (<c>ft</c>, e.g. <c>%prefix%/%topic%/</c>).
/// </summary>
internal sealed class TasmotaDevice : MqttDevice
{
    private readonly TasmotaTopics _topics;
    private readonly IReadOnlyList<(int Index, string Endpoint)> _relays;
    private readonly string? _dimmerEndpoint;
    private readonly IReadOnlyList<TasmotaSensorBinding> _sensors;
    private readonly string _onText;
    private readonly string _offText;
    private readonly string _online;
    private readonly string _offline;
    private readonly bool _singleRelay;

    public TasmotaDevice(
        DiscoveredDevice discovered,
        TasmotaTopics topics,
        IReadOnlyList<(int Index, string Endpoint)> relays,
        string? dimmerEndpoint,
        IReadOnlyList<TasmotaSensorBinding> sensors,
        (string Off, string On) stateTexts,
        (string Online, string Offline) availabilityTexts)
        : base(discovered)
    {
        _topics = topics;
        _relays = relays;
        _dimmerEndpoint = dimmerEndpoint;
        _sensors = sensors;
        (_offText, _onText) = stateTexts;
        (_online, _offline) = availabilityTexts;
        _singleRelay = relays.Count == 1 && relays[0].Index == 1;
        var topicList = new List<string>
        {
            topics.Stat("RESULT"),
            topics.Tele("STATE"),
            topics.Tele("SENSOR"),
            topics.Tele("LWT"),
            topics.Stat("STATUS10"),
            topics.Stat("STATUS8"),
        };
        if (_singleRelay)
        {
            topicList.Add(topics.Stat("POWER"));
        }

        topicList.AddRange(relays.Select(r => topics.Stat(PowerKey(r.Index))));
        Topics = topicList.Distinct(StringComparer.Ordinal).ToList();
    }

    public override IReadOnlyCollection<string> Topics { get; }

    public override bool HasAvailabilityTopic => true;

    public override IReadOnlyList<MqttReading> Read(MqttMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var topic = message.Topic;
        var text = message.Text.Trim();
        if (topic == _topics.Tele("LWT"))
        {
            return text == _online ? [new MqttReading.Reachability(Availability.Online)]
                : text == _offline ? [new MqttReading.Reachability(Availability.Offline)]
                : [];
        }

        // stat/<t>/POWER<n>: plain ON/OFF.
        foreach (var (index, endpoint) in _relays)
        {
            if (topic == _topics.Stat(PowerKey(index)) || (_singleRelay && topic == _topics.Stat("POWER")))
            {
                return OnOff(text) is { } on ? [new MqttReading.State(endpoint, CapabilityIds.Switch, "on", StateValue.Bool(on))] : [];
            }
        }

        if (Json.TryParse(text) is not { ValueKind: JsonValueKind.Object } json)
        {
            return [];
        }

        if (topic == _topics.Stat("STATUS10") || topic == _topics.Stat("STATUS8"))
        {
            return json.Get("StatusSNS") is { ValueKind: JsonValueKind.Object } sns ? ReadSensors(sns) : [];
        }

        return topic == _topics.Tele("SENSOR") ? ReadSensors(json) : ReadState(json);
    }

    /// <summary>Relay and dimmer values in a <c>RESULT</c> / <c>STATE</c> object.</summary>
    public IReadOnlyList<MqttReading> ReadState(JsonElement state)
    {
        var readings = new List<MqttReading>();
        foreach (var (index, endpoint) in _relays)
        {
            var value = state.Str(PowerKey(index)) ?? (_singleRelay ? state.Str("POWER") : null);
            if (value is not null && OnOff(value) is { } on)
            {
                readings.Add(new MqttReading.State(endpoint, CapabilityIds.Switch, "on", StateValue.Bool(on)));
            }
        }

        if (_dimmerEndpoint is not null && state.Num("Dimmer") is { } dimmer)
        {
            readings.Add(new MqttReading.State(_dimmerEndpoint, CapabilityIds.Level, "level", StateValue.Number(Conversions.Clamp(dimmer, 0, 100))));
        }

        return readings;
    }

    /// <summary>Sensor values in a <c>SENSOR</c> object (or <c>StatusSNS</c>).</summary>
    public IReadOnlyList<MqttReading> ReadSensors(JsonElement sensors)
    {
        var readings = new List<MqttReading>();
        foreach (var binding in _sensors)
        {
            if (sensors.Get(binding.Sensor) is { ValueKind: JsonValueKind.Object } sensor && sensor.Num(binding.Property) is { } number)
            {
                readings.Add(new MqttReading.State(binding.Endpoint, binding.Capability, binding.Attribute, StateValue.Number(binding.Convert(number))));
            }
        }

        return readings;
    }

    public override IReadOnlyList<MqttPublish> Refresh() =>
        [new MqttPublish(_topics.Cmnd("STATE"), string.Empty), new MqttPublish(_topics.Cmnd("STATUS"), "10")];

    protected override CommandPlan? PlanCommands(IReadOnlyList<ValidatedCommand> commands)
    {
        var publishes = new List<MqttPublish>();
        foreach (var command in commands)
        {
            if (command.Capability == CapabilityIds.Switch && _relays.FirstOrDefault(r => r.Endpoint == command.Endpoint) is { Endpoint: not null } relay)
            {
                var payload = command.Name switch
                {
                    "turnOn" => "ON",
                    "turnOff" => "OFF",
                    "toggle" => "TOGGLE",
                    _ => null,
                };
                if (payload is null)
                {
                    return null;
                }

                publishes.Add(new MqttPublish(_topics.Cmnd(_singleRelay ? "POWER" : PowerKey(relay.Index)), payload));
            }
            else if (command.Capability == CapabilityIds.Level && command.Endpoint == _dimmerEndpoint && command.Name == "setLevel")
            {
                publishes.Add(new MqttPublish(_topics.Cmnd("Dimmer"), Conversions.Format(Math.Round(command.Number("level")))));
            }
            else
            {
                return null;
            }
        }

        return new CommandPlan(publishes, []);
    }

    private static string PowerKey(int index) => string.Create(CultureInfo.InvariantCulture, $"POWER{index}");

    private bool? OnOff(string value) =>
        string.Equals(value, _onText, StringComparison.OrdinalIgnoreCase) || string.Equals(value, "ON", StringComparison.OrdinalIgnoreCase) ? true
        : string.Equals(value, _offText, StringComparison.OrdinalIgnoreCase) || string.Equals(value, "OFF", StringComparison.OrdinalIgnoreCase) ? false
        : null;
}

/// <summary>A Tasmota device's topics from its full-topic pattern (<c>ft</c>) and prefixes (<c>tp</c>).</summary>
internal sealed record TasmotaTopics(string FullTopic, string Topic, string Hostname, string Mac, string CmndPrefix, string StatPrefix, string TelePrefix)
{
    public string Cmnd(string command) => Build(CmndPrefix) + command;

    public string Stat(string command) => Build(StatPrefix) + command;

    public string Tele(string command) => Build(TelePrefix) + command;

    private string Build(string prefix)
    {
        var topic = FullTopic
            .Replace("%prefix%", prefix, StringComparison.Ordinal)
            .Replace("%topic%", Topic, StringComparison.Ordinal)
            .Replace("%hostname%", Hostname, StringComparison.Ordinal)
            .Replace("%id%", Mac.Length >= 6 ? Mac[^6..] : Mac, StringComparison.Ordinal);
        return topic.EndsWith('/') ? topic : topic + "/";
    }
}

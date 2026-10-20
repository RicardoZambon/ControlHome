using System.Diagnostics.CodeAnalysis;
using ControlHome.Domain.Devices;

namespace ControlHome.Domain.Capabilities;

/// <summary>The ids of the capabilities in <see cref="CapabilityCatalog"/>.</summary>
public static class CapabilityIds
{
    public const string Switch = "switch";
    public const string Level = "level";
    public const string Color = "color";
    public const string Thermostat = "thermostat";
    public const string Power = "power";
    public const string Energy = "energy";
    public const string Temperature = "temperature";
    public const string Humidity = "humidity";
    public const string Contact = "contact";
    public const string Motion = "motion";
    public const string Occupancy = "occupancy";
    public const string Lock = "lock";
    public const string DoorOpener = "doorOpener";
    public const string Cover = "cover";
    public const string AlarmPanel = "alarmPanel";
    public const string MediaPlayer = "mediaPlayer";
    public const string Button = "button";
    public const string Battery = "battery";
    public const string Presence = "presence";
    public const string Smoke = "smoke";
    public const string WaterLeak = "waterLeak";
    public const string AirQuality = "airQuality";
    public const string Camera = "camera";
    public const string ApplianceCycle = "applianceCycle";

    /// <summary>Vendor properties without a mapping: the definition comes from the connector, per endpoint.</summary>
    public const string Generic = "generic";
}

/// <summary>
/// Every capability the home understands, defined in code and served by <c>GET /api/capabilities</c>. Values are in
/// canonical units (<see cref="Units"/>). Safety-critical commands: <c>lock.unlock</c>, <c>doorOpener.open</c>,
/// <c>alarmPanel.disarm</c>, and <c>cover.open</c> / <c>cover.setPosition</c> on a <c>gate</c> or <c>garage</c>.
/// </summary>
public static class CapabilityCatalog
{
    private static readonly NumberSchema _percent = new(0, 100, 1, Units.Percent, Integer: true);
    private static readonly string[] _gateClasses = [DeviceClasses.Gate, DeviceClasses.Garage];

    public static CapabilityDefinition Switch { get; } = new(
        CapabilityIds.Switch,
        1,
        [Attribute("on", BoolSchema.Instance, recorded: true, writable: true)],
        [
            Command("turnOn") with { Expects = Expect("on", [StateValue.True]) },
            Command("turnOff") with { Expects = Expect("on", [StateValue.False]) },
            Command("toggle"),
        ]);

    public static CapabilityDefinition Level { get; } = new(
        CapabilityIds.Level,
        1,
        [Attribute("level", _percent, recorded: true, writable: true)],
        [
            Command(
                "setLevel",
                Argument("level", _percent),
                Argument("transitionMs", new NumberSchema(0, 60_000, 1, Units.Millisecond, Integer: true), required: false)),
        ]);

    public static CapabilityDefinition Color { get; } = new(
        CapabilityIds.Color,
        1,
        [
            Attribute("hue", new NumberSchema(0, 360, null, Units.Degree), writable: true),
            Attribute("saturation", _percent, writable: true),
            Attribute("colorTemperature", new NumberSchema(1000, 10_000, 1, Units.Kelvin, Integer: true), writable: true),
        ],
        [
            Command(
                "setColor",
                Argument("hue", new NumberSchema(0, 360, null, Units.Degree)),
                Argument("saturation", _percent)),
            Command("setColorTemperature", Argument("colorTemperature", new NumberSchema(1000, 10_000, 1, Units.Kelvin, Integer: true))),
        ]);

    public static CapabilityDefinition Thermostat { get; } = new(
        CapabilityIds.Thermostat,
        1,
        [
            Attribute("mode", ThermostatModes, recorded: true, writable: true),
            Attribute("targetTemperature", TargetTemperature, recorded: true, writable: true),
            Measurement("currentTemperature", new NumberSchema(null, null, null, Units.Celsius)),
            Attribute("fanMode", FanModes, writable: true),
        ],
        [
            Command("setMode", Argument("mode", ThermostatModes)),
            Command("setTargetTemperature", Argument("targetTemperature", TargetTemperature)),
            Command("setFanMode", Argument("fanMode", FanModes)),
        ]);

    public static CapabilityDefinition Power { get; } = new(
        CapabilityIds.Power,
        1,
        [Measurement("power", new NumberSchema(null, null, null, Units.Watt))],
        []);

    public static CapabilityDefinition Energy { get; } = new(
        CapabilityIds.Energy,
        1,
        [Attribute("energy", new NumberSchema(0, null, null, Units.WattHour), StateClass.TotalIncreasing, recorded: true)],
        []);

    public static CapabilityDefinition Temperature { get; } = new(
        CapabilityIds.Temperature,
        1,
        [Measurement("temperature", new NumberSchema(null, null, null, Units.Celsius))],
        []);

    public static CapabilityDefinition Humidity { get; } = new(
        CapabilityIds.Humidity,
        1,
        [Measurement("humidity", new NumberSchema(0, 100, null, Units.Percent))],
        []);

    public static CapabilityDefinition Contact { get; } = new(
        CapabilityIds.Contact,
        1,
        [Attribute("open", BoolSchema.Instance, recorded: true)],
        []);

    public static CapabilityDefinition Motion { get; } = new(
        CapabilityIds.Motion,
        1,
        [Attribute("motion", BoolSchema.Instance, recorded: true)],
        []);

    public static CapabilityDefinition Occupancy { get; } = new(
        CapabilityIds.Occupancy,
        1,
        [Attribute("occupied", BoolSchema.Instance, recorded: true)],
        []);

    public static CapabilityDefinition Lock { get; } = new(
        CapabilityIds.Lock,
        1,
        [Attribute("lockState", new EnumSchema(["locked", "unlocked", "jammed", "unknown"]), recorded: true)],
        [
            Command("lock") with { FailSafe = true, Expects = Expect("lockState", ["locked"], fails: ["jammed"]) },
            Command("unlock") with { SafetyCritical = true, Expects = Expect("lockState", ["unlocked"], fails: ["jammed"]) },
        ]);

    public static CapabilityDefinition DoorOpener { get; } = new(
        CapabilityIds.DoorOpener,
        1,
        [],
        [Command("open") with { Confirmation = ConfirmationMode.AckIsFinal, SafetyCritical = true }]);

    public static CapabilityDefinition Cover { get; } = new(
        CapabilityIds.Cover,
        1,
        [
            Attribute("position", _percent, recorded: true, writable: true),
            Attribute("coverState", new EnumSchema(["open", "closed", "opening", "closing", "stopped", "unknown"]), recorded: true),
        ],
        [
            Command("open") with { SafetyCriticalForDeviceClasses = _gateClasses, Expects = Expect("coverState", ["open"]) },
            Command("close") with { FailSafe = true, Expects = Expect("coverState", ["closed"]) },
            Command("stop") with { Expects = Expect("coverState", ["stopped", "open", "closed"]) }, // not moving any more

            // Opening part-way is still opening: gated like open on a gate or garage.
            Command("setPosition", Argument("position", _percent)) with { SafetyCriticalForDeviceClasses = _gateClasses },
        ]);

    public static CapabilityDefinition AlarmPanel { get; } = new(
        CapabilityIds.AlarmPanel,
        1,
        [
            Attribute(
                "armState",
                new EnumSchema(["disarmed", "armed_home", "armed_away", "armed_night", "triggered", "arming"]),
                recorded: true),
        ],
        [
            Command("arm", Argument("mode", new EnumSchema(["home", "away", "night"]))) with
            {
                Expects = Expect("armState", ["armed_home", "armed_away", "armed_night"]),
            },
            Command("disarm") with { SafetyCritical = true, Expects = Expect("armState", ["disarmed"]) },
        ]);

    public static CapabilityDefinition MediaPlayer { get; } = new(
        CapabilityIds.MediaPlayer,
        1,
        [
            Attribute("power", BoolSchema.Instance, recorded: true, writable: true),
            Attribute("volume", _percent, writable: true),
            Attribute("muted", BoolSchema.Instance, writable: true),
            Attribute("source", new StringSchema(128), writable: true),
            Attribute("app", new StringSchema(128), writable: true),
            Attribute("playbackState", new EnumSchema(["idle", "playing", "paused", "stopped", "buffering", "unknown"])),
        ],
        [
            // A TV may need Wake-on-LAN and a boot.
            Command("powerOn") with { Timeout = TimeSpan.FromSeconds(90), Expects = Expect("power", [StateValue.True]) },
            Command("powerOff") with { Expects = Expect("power", [StateValue.False]) },
            Command("setVolume", Argument("volume", _percent)),
            Momentary("volumeUp"),
            Momentary("volumeDown"),
            Command("setMuted", Argument("muted", BoolSchema.Instance)),
            Command("selectSource", Argument("source", new StringSchema(128))),
            Command("launchApp", Argument("app", new StringSchema(128))),
            Command("play") with { Expects = Expect("playbackState", ["playing"]) },
            Command("pause") with { Expects = Expect("playbackState", ["paused"]) },
            Command("stop") with { Expects = Expect("playbackState", ["stopped", "idle"]) },
            Momentary("next"),
            Momentary("previous"),
        ]);

    public static CapabilityDefinition Button { get; } = new(
        CapabilityIds.Button,
        1,
        [Attribute("press", new MomentarySchema { Events = ["single", "double", "long"] })],
        []);

    public static CapabilityDefinition Battery { get; } = new(
        CapabilityIds.Battery,
        1,
        [Attribute("battery", _percent, StateClass.Measurement, AttributeCategory.Diagnostic, recorded: true)],
        []);

    public static CapabilityDefinition Presence { get; } = new(
        CapabilityIds.Presence,
        1,
        [Attribute("present", BoolSchema.Instance, recorded: true)],
        []);

    public static CapabilityDefinition Smoke { get; } = new(
        CapabilityIds.Smoke,
        1,
        [Attribute("smoke", BoolSchema.Instance, recorded: true)],
        []);

    public static CapabilityDefinition WaterLeak { get; } = new(
        CapabilityIds.WaterLeak,
        1,
        [Attribute("leak", BoolSchema.Instance, recorded: true)],
        []);

    public static CapabilityDefinition AirQuality { get; } = new(
        CapabilityIds.AirQuality,
        1,
        [
            Measurement("pm25", new NumberSchema(0, null, null, Units.MicrogramsPerCubicMeter)),
            Measurement("co2", new NumberSchema(0, null, null, Units.PartsPerMillion)),
            Measurement("voc", new NumberSchema(0, null, null, Units.PartsPerBillion)),
        ],
        []);

    public static CapabilityDefinition Camera { get; } = new(
        CapabilityIds.Camera,
        1,
        [Attribute("streamAvailable", BoolSchema.Instance)],
        [Momentary("snapshot")]);

    public static CapabilityDefinition ApplianceCycle { get; } = new(
        CapabilityIds.ApplianceCycle,
        1,
        [
            Attribute("machineState", new EnumSchema(["idle", "running", "paused", "finished", "error"]), recorded: true),
            Attribute("phase", new StringSchema(64)),
            Attribute("remainingMinutes", new NumberSchema(0, null, 1, Units.Minute, Integer: true)),
            Attribute("remoteControlEnabled", BoolSchema.Instance, category: AttributeCategory.Diagnostic),
        ],
        [
            Command("start") with { GateAttribute = "remoteControlEnabled", Expects = Expect("machineState", ["running"]) },
            Command("pause") with { GateAttribute = "remoteControlEnabled", Expects = Expect("machineState", ["paused"]) },
            Command("stop") with { GateAttribute = "remoteControlEnabled", Expects = Expect("machineState", ["idle", "finished"]) },
        ]);

    /// <summary>
    /// Placeholder for vendor properties without a mapping: no attributes or commands of its own. Each endpoint's
    /// <c>EndpointCapability.GenericDefinitionJson</c> holds the connector-supplied <see cref="CapabilityDefinition"/>
    /// (id <c>generic</c>); writable attributes there come with a setter command (see
    /// <see cref="CapabilityDefinition.FindSetter"/>).
    /// </summary>
    public static CapabilityDefinition Generic { get; } = new(CapabilityIds.Generic, 1, [], []);

    /// <summary>Every capability, in a stable order.</summary>
    public static IReadOnlyList<CapabilityDefinition> All { get; } =
    [
        Switch, Level, Color, Thermostat, Power, Energy, Temperature, Humidity, Contact, Motion, Occupancy, Lock, DoorOpener,
        Cover, AlarmPanel, MediaPlayer, Button, Battery, Presence, Smoke, WaterLeak, AirQuality, Camera, ApplianceCycle, Generic,
    ];

    private static readonly Dictionary<string, CapabilityDefinition> _byId = All.ToDictionary(c => c.Id, StringComparer.Ordinal);

    private static EnumSchema ThermostatModes => new(["off", "cool", "heat", "auto", "dry", "fan"]);

    private static EnumSchema FanModes => new(["auto", "quiet", "low", "medium", "high", "turbo"]);

    private static NumberSchema TargetTemperature => new(5, 35, 0.5, Units.Celsius);

    /// <summary>The capability with id <paramref name="id"/>, or <c>null</c>.</summary>
    public static CapabilityDefinition? Find(string id) => _byId.GetValueOrDefault(id);

    public static bool TryGet(string id, [NotNullWhen(true)] out CapabilityDefinition? definition) =>
        _byId.TryGetValue(id, out definition);

    private static AttributeDefinition Attribute(
        string key,
        ValueSchema schema,
        StateClass stateClass = StateClass.None,
        AttributeCategory category = AttributeCategory.Primary,
        bool recorded = false,
        bool writable = false) =>
        new(key, schema, (schema as NumberSchema)?.Unit, stateClass, category, recorded, writable);

    private static AttributeDefinition Measurement(string key, NumberSchema schema) =>
        Attribute(key, schema, StateClass.Measurement, recorded: true);

    private static ArgumentDefinition Argument(string key, ValueSchema schema, bool required = true) => new(key, schema, required);

    private static CommandDefinition Command(string name, params ArgumentDefinition[] arguments) =>
        new(name, arguments, ConfirmationMode.AwaitState, null, SafetyCritical: false, FailSafe: false);

    private static CommandDefinition Momentary(string name) => Command(name) with { Confirmation = ConfirmationMode.AckIsFinal };

    private static ExpectedState Expect(string attribute, StateValue[] confirms, StateValue[]? fails = null) =>
        new(attribute, confirms, fails ?? []);

    private static ExpectedState Expect(string attribute, string[] confirms, string[]? fails = null) =>
        new(attribute, [.. confirms.Select(StateValue.String)], [.. (fails ?? []).Select(StateValue.String)]);
}

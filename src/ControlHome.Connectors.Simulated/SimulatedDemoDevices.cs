using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;

namespace ControlHome.Connectors.Simulated;

/// <summary>
/// The devices the <c>demo</c> preset adds after the basic five: a color lamp, a washing machine, a TV, a sleepy motion
/// sensor, a 3-gang switch (three endpoints with their own power), an NVR with four cameras (child devices with bundled
/// snapshot images), a gate (safety-critical opening) with its contact, blinds, a generic-only water tank, a bathroom
/// climate sensor and a fridge plug — 20 devices in all.
/// </summary>
internal static class SimulatedDemoDevices
{
    public const string ColorLight = "light-2";
    public const string Washer = "washer-1";
    public const string Tv = "tv-1";
    public const string Motion = "motion-1";
    public const string Switch3 = "switch3-1";
    public const string Nvr = "nvr-1";
    public const string Gate = "gate-1";
    public const string Blinds = "blinds-1";
    public const string Tank = "tank-1";
    public const string Climate = "climate-1";
    public const string FridgePlug = "plug-2";

    /// <summary>The cameras' external ids (children of <see cref="Nvr"/>), in channel order.</summary>
    public static IReadOnlyList<string> Cameras { get; } = ["cam-1", "cam-2", "cam-3", "cam-4"];

    /// <summary>The demo devices in report order; constructors draw from <paramref name="random"/> in this order.</summary>
    public static IReadOnlyList<SimulatedDevice> Create(int seed, Random random) =>
    [
        new SimulatedColorLight(seed, random),
        new SimulatedWasher(seed),
        new SimulatedTv(seed),
        new SimulatedMotionSensor(seed, random),
        new SimulatedThreeGangSwitch(seed),
        new SimulatedNvr(seed),
        new SimulatedCamera(Cameras[0], 12, "Gate camera", "Garage", seed),
        new SimulatedCamera(Cameras[1], 13, "Garage camera", "Garage", seed),
        new SimulatedCamera(Cameras[2], 14, "Backyard camera", "Backyard", seed),
        new SimulatedCamera(Cameras[3], 15, "Front porch camera", "Entrance", seed),
        new SimulatedCover(Gate, 16, "Gate", DeviceClasses.Gate, "Simulated sliding gate", "Garage", seed, initialPosition: 0, withContact: true),
        new SimulatedCover(Blinds, 17, "Living room blinds", DeviceClasses.Blind, "Simulated roller blind", "Living room", seed, initialPosition: 100, withContact: false),
        new SimulatedWaterTank(seed),
        new SimulatedClimateSensor(seed, random),
        new SimulatedPlug(FridgePlug, 20, "Fridge plug", "Kitchen", seed, random, minBaseW: 90, spreadW: 40),
    ];
}

/// <summary>A color lamp: <c>switch</c> + <c>level</c> + <c>color</c> (hue/saturation or a white temperature 2700–6500 K).</summary>
internal sealed class SimulatedColorLight : SimulatedDevice
{
    private bool _on = true;
    private double _level;
    private double _hue;
    private double _saturation = 80;
    private double _colorTemperatureK = 3000;

    public SimulatedColorLight(int seed, Random random)
        : base(SimulatedDemoDevices.ColorLight, 6, seed)
    {
        _level = random.Next(30, 81);
        _hue = random.Next(0, 360);
    }

    public override string Name => "Bedroom lamp";

    public override string DeviceClass => DeviceClasses.Light;

    public override string Model => "Simulated color bulb";

    public override string SuggestedRoom => "Bedroom";

    public override IReadOnlyList<CapabilityBinding> Capabilities { get; } =
    [
        CapabilityBinding.Of(CapabilityIds.Switch),
        CapabilityBinding.Of(CapabilityIds.Level, Constraints(("level", new AttributeConstraint(Min: 1)))),
        CapabilityBinding.Of(CapabilityIds.Color, Constraints(("colorTemperature", new AttributeConstraint(Min: 2700, Max: 6500)))),
    ];

    public override IEnumerable<SimulatedValue> Snapshot() =>
    [
        new(CapabilityIds.Switch, "on", StateValue.Bool(_on)),
        new(CapabilityIds.Level, "level", StateValue.Number(_level)),
        new(CapabilityIds.Color, "hue", StateValue.Number(_hue)),
        new(CapabilityIds.Color, "saturation", StateValue.Number(_saturation)),
        new(CapabilityIds.Color, "colorTemperature", StateValue.Number(_colorTemperatureK)),
    ];

    public override void Apply(DeviceCommand command, DateTimeOffset now)
    {
        switch (command.Command)
        {
            case "turnOn":
                _on = true;
                break;
            case "turnOff":
                _on = false;
                break;
            case "toggle":
                _on = !_on;
                break;
            case "setLevel":
                _level = Number(command, "level");
                _on = true;
                break;
            case "setColor":
                _hue = Number(command, "hue");
                _saturation = Number(command, "saturation");
                _on = true;
                break;
            case "setColorTemperature":
                _colorTemperatureK = Number(command, "colorTemperature");
                _saturation = 0;
                _on = true;
                break;
            default:
                throw Unsupported(command);
        }
    }
}

/// <summary>
/// A washing machine: <c>applianceCycle</c> + <c>power</c>. It starts mid-cycle; the remaining time counts down with the
/// clock (wash → rinse → spin → finished) and the power follows the phase. Remote control is enabled, so start, pause and
/// stop are accepted.
/// </summary>
internal sealed class SimulatedWasher(int seed) : SimulatedDevice(SimulatedDemoDevices.Washer, 7, seed)
{
    private const int CycleMinutes = 58;
    private const double StandbyW = 1.5;

    private string _state = "running";
    private int _remainingMinutes = 42;
    private double _powerW;
    private TimeSpan _elapsed;
    private DateTimeOffset _lastTick;

    public override string Name => "Washing machine";

    public override string DeviceClass => DeviceClasses.Washer;

    public override string Model => "Simulated front loader";

    public override string SuggestedRoom => "Laundry";

    public override IReadOnlyList<CapabilityBinding> Capabilities { get; } =
    [
        CapabilityBinding.Of(CapabilityIds.ApplianceCycle),
        CapabilityBinding.Of(CapabilityIds.Power),
    ];

    private string? Phase => _state is "running" or "paused"
        ? _remainingMinutes > 25 ? "wash" : _remainingMinutes > 10 ? "rinse" : "spin"
        : null;

    public override IEnumerable<SimulatedValue> Snapshot() =>
    [
        new(CapabilityIds.ApplianceCycle, "machineState", StateValue.String(_state)),
        new(CapabilityIds.ApplianceCycle, "phase", Phase is { } phase ? StateValue.String(phase) : StateValue.Null),
        new(CapabilityIds.ApplianceCycle, "remainingMinutes", StateValue.Number(_remainingMinutes)),
        new(CapabilityIds.ApplianceCycle, "remoteControlEnabled", StateValue.True),
        new(CapabilityIds.Power, "power", StateValue.Number(_powerW)),
    ];

    public override void Start(DateTimeOffset now)
    {
        _lastTick = now;
        _powerW = PhasePower(0);
    }

    public override void Tick(DateTimeOffset now, Random random)
    {
        var step = now - _lastTick;
        _lastTick = now;
        if (_state == "running")
        {
            _elapsed += step;
            while (_elapsed >= TimeSpan.FromMinutes(1) && _remainingMinutes > 0)
            {
                _elapsed -= TimeSpan.FromMinutes(1);
                _remainingMinutes--;
            }

            if (_remainingMinutes == 0)
            {
                _state = "finished";
            }
        }

        _powerW = PhasePower(random.NextDouble());
    }

    public override void Apply(DeviceCommand command, DateTimeOffset now)
    {
        switch (command.Command)
        {
            case "start":
                if (_state is "idle" or "finished")
                {
                    _remainingMinutes = CycleMinutes;
                    _elapsed = TimeSpan.Zero;
                }

                _state = "running";
                break;
            case "pause":
                if (_state == "running")
                {
                    _state = "paused";
                }

                break;
            case "stop":
                _state = "idle";
                _remainingMinutes = 0;
                _elapsed = TimeSpan.Zero;
                break;
            default:
                throw Unsupported(command);
        }

        _powerW = PhasePower(0.5);
    }

    /// <summary>The power of the current phase with up to ±10 % noise (<paramref name="noise"/> in 0..1).</summary>
    private double PhasePower(double noise)
    {
        double basePower = _state != "running" ? StandbyW : Phase switch
        {
            "wash" => 450,
            "rinse" => 120,
            _ => 380,
        };
        return basePower <= StandbyW ? StandbyW : Math.Round(basePower * (0.9 + (noise * 0.2)), 1);
    }
}

/// <summary>A TV: <c>mediaPlayer</c> (power, volume, mute, source, app, playback).</summary>
internal sealed class SimulatedTv(int seed) : SimulatedDevice(SimulatedDemoDevices.Tv, 8, seed)
{
    private bool _power = true;
    private double _volume = 18;
    private bool _muted;
    private string _source = "HDMI 1";
    private string _app = "Netflix";
    private string _playback = "playing";

    public override string Name => "Living room TV";

    public override string DeviceClass => DeviceClasses.Tv;

    public override string Model => "Simulated smart TV";

    public override string SuggestedRoom => "Living room";

    public override IReadOnlyList<CapabilityBinding> Capabilities { get; } = [CapabilityBinding.Of(CapabilityIds.MediaPlayer)];

    public override IEnumerable<SimulatedValue> Snapshot() =>
    [
        new(CapabilityIds.MediaPlayer, "power", StateValue.Bool(_power)),
        new(CapabilityIds.MediaPlayer, "volume", StateValue.Number(_volume)),
        new(CapabilityIds.MediaPlayer, "muted", StateValue.Bool(_muted)),
        new(CapabilityIds.MediaPlayer, "source", StateValue.String(_source)),
        new(CapabilityIds.MediaPlayer, "app", StateValue.String(_app)),
        new(CapabilityIds.MediaPlayer, "playbackState", StateValue.String(_power ? _playback : "idle")),
    ];

    public override void Apply(DeviceCommand command, DateTimeOffset now)
    {
        switch (command.Command)
        {
            case "powerOn":
                _power = true;
                break;
            case "powerOff":
                _power = false;
                break;
            case "setVolume":
                _volume = Number(command, "volume");
                break;
            case "volumeUp":
                _volume = Math.Min(100, _volume + 1);
                break;
            case "volumeDown":
                _volume = Math.Max(0, _volume - 1);
                break;
            case "setMuted":
                _muted = Flag(command, "muted");
                break;
            case "selectSource":
                _source = Text(command, "source");
                break;
            case "launchApp":
                _app = Text(command, "app");
                _playback = "playing";
                _power = true;
                break;
            case "play":
                _playback = "playing";
                break;
            case "pause":
                _playback = "paused";
                break;
            case "stop":
                _playback = "stopped";
                break;
            case "next":
            case "previous":
                break;
            default:
                throw Unsupported(command);
        }
    }
}

/// <summary>
/// A battery motion sensor that sleeps: it reports only when it wakes up on motion (about once every few minutes), clears
/// the motion 30 s later, and its battery reading changes only when it wakes.
/// </summary>
internal sealed class SimulatedMotionSensor : SimulatedDevice
{
    private static readonly TimeSpan _clearAfter = TimeSpan.FromSeconds(30);

    private bool _motion;
    private DateTimeOffset _lastMotion;
    private double _batteryPct;
    private double _reportedBatteryPct;

    public SimulatedMotionSensor(int seed, Random random)
        : base(SimulatedDemoDevices.Motion, 9, seed)
    {
        _batteryPct = random.Next(55, 96);
        _reportedBatteryPct = _batteryPct;
    }

    public override string Name => "Office motion sensor";

    public override string DeviceClass => DeviceClasses.Sensor;

    public override string Model => "Simulated motion sensor (battery)";

    public override string SuggestedRoom => "Office";

    public override IReadOnlyList<CapabilityBinding> Capabilities { get; } =
    [
        CapabilityBinding.Of(CapabilityIds.Motion),
        CapabilityBinding.Of(CapabilityIds.Battery),
    ];

    public override IEnumerable<SimulatedValue> Snapshot() =>
    [
        new(CapabilityIds.Motion, "motion", StateValue.Bool(_motion)),
        new(CapabilityIds.Battery, "battery", StateValue.Number(Math.Round(_reportedBatteryPct))),
    ];

    public override void Tick(DateTimeOffset now, Random random)
    {
        _batteryPct = Math.Max(0, _batteryPct - 0.001);
        if (_motion && now - _lastMotion >= _clearAfter)
        {
            _motion = false;
        }
        else if (!_motion && random.NextDouble() < 0.03)
        {
            _motion = true;
            _lastMotion = now;
            _reportedBatteryPct = _batteryPct; // read while awake
        }
    }

    public override void Apply(DeviceCommand command, DateTimeOffset now) => throw Unsupported(command);
}

/// <summary>
/// A 3-gang wall switch: one device with three endpoints (<c>l1</c> ceiling, <c>l2</c> wall lights, <c>l3</c> porch), each
/// with <c>switch</c> and its own <c>power</c> reading.
/// </summary>
internal sealed class SimulatedThreeGangSwitch(int seed) : SimulatedDevice(SimulatedDemoDevices.Switch3, 10, seed)
{
    private static readonly IReadOnlyList<CapabilityBinding> _channel =
    [
        CapabilityBinding.Of(CapabilityIds.Switch),
        CapabilityBinding.Of(CapabilityIds.Power),
    ];

    private readonly Channel[] _channels =
    [
        new("l1", "Ceiling", 60, on: true),
        new("l2", "Wall lights", 24, on: false),
        new("l3", "Porch", 15, on: true),
    ];

    public override string Name => "Living room wall switch";

    public override string DeviceClass => DeviceClasses.Switch;

    public override string Model => "Simulated 3-gang switch";

    public override string SuggestedRoom => "Living room";

    public override IReadOnlyList<DiscoveredEndpoint> Endpoints =>
        [.. _channels.Select(c => new DiscoveredEndpoint(c.Key, c.Name, _channel))];

    public override IEnumerable<SimulatedValue> Snapshot() =>
        _channels.SelectMany(c => new SimulatedValue[]
        {
            new(c.Key, CapabilityIds.Switch, "on", StateValue.Bool(c.On)),
            new(c.Key, CapabilityIds.Power, "power", StateValue.Number(c.On ? c.PowerW : 0)),
        });

    public override void Tick(DateTimeOffset now, Random random)
    {
        foreach (var channel in _channels)
        {
            channel.PowerW = Math.Round(channel.RatedW + (random.NextDouble() - 0.5), 1);
        }
    }

    public override void Apply(DeviceCommand command, DateTimeOffset now)
    {
        var channel = _channels.Single(c => c.Key == command.EndpointKey);
        channel.On = command.Command switch
        {
            "turnOn" => true,
            "turnOff" => false,
            "toggle" => !channel.On,
            _ => throw Unsupported(command),
        };
    }

    private sealed class Channel(string key, string name, double ratedW, bool on)
    {
        public string Key { get; } = key;

        public string Name { get; } = name;

        public double RatedW { get; } = ratedW;

        public bool On { get; set; } = on;

        public double PowerW { get; set; } = ratedW;
    }
}

/// <summary>
/// A network video recorder, parent of the four cameras. It has no controls; its storage use and the channels recording
/// are vendor properties without a catalogue capability, reported through <c>generic</c>.
/// </summary>
internal sealed class SimulatedNvr(int seed) : SimulatedDevice(SimulatedDemoDevices.Nvr, 11, seed)
{
    private static readonly CapabilityDefinition _definition = new(
        CapabilityIds.Generic,
        1,
        [
            new AttributeDefinition("storageUsed", new NumberSchema(0, 100, null, Units.Percent), Units.Percent, StateClass.Measurement, AttributeCategory.Diagnostic, Recorded: true, Writable: false),
            new AttributeDefinition("recordingChannels", new NumberSchema(0, 16, 1, null, Integer: true), null, StateClass.None, AttributeCategory.Primary, Recorded: false, Writable: false),
        ],
        []);

    private double _storageUsedPct = 63.2;

    public override string Name => "Recorder";

    public override string DeviceClass => DeviceClasses.Nvr;

    public override string Model => "Simulated 4-channel NVR";

    public override string SuggestedRoom => "Garage";

    public override IReadOnlyList<CapabilityBinding> Capabilities { get; } =
        [new CapabilityBinding(CapabilityIds.Generic, null, _definition)];

    public override IEnumerable<SimulatedValue> Snapshot() =>
    [
        new(CapabilityIds.Generic, "storageUsed", StateValue.Number(Math.Round(_storageUsedPct, 1))),
        new(CapabilityIds.Generic, "recordingChannels", StateValue.Number(SimulatedDemoDevices.Cameras.Count)),
    ];

    public override void Tick(DateTimeOffset now, Random random)
    {
        // Recording fills the disk; the oldest footage is overwritten at 95 %.
        _storageUsedPct += 0.002;
        if (_storageUsedPct >= 95)
        {
            _storageUsedPct = 60;
        }
    }

    public override void Apply(DeviceCommand command, DateTimeOffset now) => throw Unsupported(command);
}

/// <summary>
/// A camera on an NVR channel (a child device): <c>camera</c> (stream available, snapshot) + <c>motion</c>. Its snapshot is
/// a bundled synthetic image (<see cref="SimulatedSnapshots"/>).
/// </summary>
internal sealed class SimulatedCamera(string externalId, int index, string name, string room, int seed)
    : SimulatedDevice(externalId, index, seed)
{
    private bool _motion;
    private DateTimeOffset _motionSince;

    public override string Name { get; } = name;

    public override string DeviceClass => DeviceClasses.Camera;

    public override string Model => "Simulated bullet camera";

    public override string SuggestedRoom { get; } = room;

    public override string? ParentExternalId => SimulatedDemoDevices.Nvr;

    public override IReadOnlyList<CapabilityBinding> Capabilities { get; } =
    [
        CapabilityBinding.Of(CapabilityIds.Camera),
        CapabilityBinding.Of(CapabilityIds.Motion),
    ];

    /// <summary>When the last snapshot was taken (the snapshot command), or <c>null</c>.</summary>
    public DateTimeOffset? LastSnapshotAt { get; private set; }

    public override IEnumerable<SimulatedValue> Snapshot() =>
    [
        new(CapabilityIds.Camera, "streamAvailable", StateValue.True),
        new(CapabilityIds.Motion, "motion", StateValue.Bool(_motion)),
    ];

    public override void Tick(DateTimeOffset now, Random random)
    {
        if (_motion && now - _motionSince >= TimeSpan.FromSeconds(20))
        {
            _motion = false;
        }
        else if (!_motion && random.NextDouble() < 0.01)
        {
            _motion = true;
            _motionSince = now;
        }
    }

    public override void Apply(DeviceCommand command, DateTimeOffset now)
    {
        if (command.Command != "snapshot")
        {
            throw Unsupported(command);
        }

        LastSnapshotAt = now;
    }
}

/// <summary>
/// A motorized cover: <c>cover</c> (and, for the gate, a <c>contact</c> that is open whenever it is not fully closed). It
/// moves 25 % per tick towards its target: <c>opening</c>/<c>closing</c> while moving, then <c>open</c>, <c>closed</c> or
/// <c>stopped</c> part-way. On a gate, opening is safety-critical (step-up PIN, at-most-once).
/// </summary>
internal sealed class SimulatedCover(
    string externalId,
    int index,
    string name,
    string deviceClass,
    string model,
    string room,
    int seed,
    double initialPosition,
    bool withContact) : SimulatedDevice(externalId, index, seed)
{
    private const double StepPct = 25;

    private double _position = initialPosition;
    private double _target = initialPosition;
    private string _state = Rest(initialPosition);

    public override string Name { get; } = name;

    public override string DeviceClass { get; } = deviceClass;

    public override string Model { get; } = model;

    public override string SuggestedRoom { get; } = room;

    public override IReadOnlyList<CapabilityBinding> Capabilities { get; } = withContact
        ? [CapabilityBinding.Of(CapabilityIds.Cover), CapabilityBinding.Of(CapabilityIds.Contact)]
        : [CapabilityBinding.Of(CapabilityIds.Cover)];

    public override IEnumerable<SimulatedValue> Snapshot()
    {
        yield return new(CapabilityIds.Cover, "position", StateValue.Number(_position));
        yield return new(CapabilityIds.Cover, "coverState", StateValue.String(_state));
        if (withContact)
        {
            yield return new(CapabilityIds.Contact, "open", StateValue.Bool(_position > 0));
        }
    }

    public override void Tick(DateTimeOffset now, Random random)
    {
        if (_position == _target)
        {
            return;
        }

        _position = _target > _position ? Math.Min(_target, _position + StepPct) : Math.Max(_target, _position - StepPct);
        if (_position == _target)
        {
            _state = Rest(_position);
        }
    }

    public override void Apply(DeviceCommand command, DateTimeOffset now)
    {
        switch (command.Command)
        {
            case "open":
                MoveTo(100);
                break;
            case "close":
                MoveTo(0);
                break;
            case "stop":
                _target = _position;
                _state = _position is 0d or 100d ? Rest(_position) : "stopped";
                break;
            case "setPosition":
                MoveTo(Number(command, "position"));
                break;
            default:
                throw Unsupported(command);
        }
    }

    private void MoveTo(double target)
    {
        _target = target;
        _state = target > _position ? "opening" : target < _position ? "closing" : Rest(_position);
    }

    private static string Rest(double position) => position switch
    {
        >= 100d => "open",
        <= 0d => "closed",
        _ => "stopped",
    };
}

/// <summary>
/// A device known only through <c>generic</c> (no catalogue capability fits): a water tank with its level and a pump that
/// refills it (<c>setPump</c>). The level drops slowly, rises while the pump runs, and the pump stops itself at 95 %.
/// </summary>
internal sealed class SimulatedWaterTank(int seed) : SimulatedDevice(SimulatedDemoDevices.Tank, 18, seed)
{
    private static readonly CapabilityDefinition _definition = new(
        CapabilityIds.Generic,
        1,
        [
            new AttributeDefinition("level", new NumberSchema(0, 100, null, Units.Percent), Units.Percent, StateClass.Measurement, AttributeCategory.Primary, Recorded: true, Writable: false),
            new AttributeDefinition("pump", BoolSchema.Instance, null, StateClass.None, AttributeCategory.Primary, Recorded: true, Writable: true),
        ],
        [
            new CommandDefinition("setPump", [new ArgumentDefinition("pump", BoolSchema.Instance, Required: true, Attribute: "pump")], ConfirmationMode.AwaitState, null, SafetyCritical: false, FailSafe: false),
        ]);

    private double _levelPct = 72;
    private bool _pump;

    public override string Name => "Water tank";

    public override string DeviceClass => DeviceClasses.Sensor;

    public override string Model => "Simulated tank controller";

    public override string SuggestedRoom => "Laundry";

    public override IReadOnlyList<CapabilityBinding> Capabilities { get; } =
        [new CapabilityBinding(CapabilityIds.Generic, null, _definition)];

    public override IEnumerable<SimulatedValue> Snapshot() =>
    [
        new(CapabilityIds.Generic, "level", StateValue.Number(Math.Round(_levelPct, 1))),
        new(CapabilityIds.Generic, "pump", StateValue.Bool(_pump)),
    ];

    public override void Tick(DateTimeOffset now, Random random)
    {
        _levelPct = Math.Clamp(_levelPct + (_pump ? 0.5 : -0.02), 0, 100);
        if (_pump && _levelPct >= 95)
        {
            _pump = false;
        }
    }

    public override void Apply(DeviceCommand command, DateTimeOffset now)
    {
        if (command.Command != "setPump")
        {
            throw Unsupported(command);
        }

        _pump = Flag(command, "pump");
    }
}

/// <summary>A bathroom climate sensor: <c>temperature</c> + <c>humidity</c> + <c>battery</c>, slow random walks.</summary>
internal sealed class SimulatedClimateSensor : SimulatedDevice
{
    private double _temperatureC;
    private double _humidityPct;
    private double _batteryPct;

    public SimulatedClimateSensor(int seed, Random random)
        : base(SimulatedDemoDevices.Climate, 19, seed)
    {
        _temperatureC = Math.Round(23 + (random.NextDouble() * 3), 1);
        _humidityPct = Math.Round(60 + (random.NextDouble() * 15), 0);
        _batteryPct = random.Next(60, 101);
    }

    public override string Name => "Bathroom sensor";

    public override string DeviceClass => DeviceClasses.Sensor;

    public override string Model => "Simulated temperature and humidity sensor";

    public override string SuggestedRoom => "Bathroom";

    public override IReadOnlyList<CapabilityBinding> Capabilities { get; } =
    [
        CapabilityBinding.Of(CapabilityIds.Temperature),
        CapabilityBinding.Of(CapabilityIds.Humidity),
        CapabilityBinding.Of(CapabilityIds.Battery),
    ];

    public override IEnumerable<SimulatedValue> Snapshot() =>
    [
        new(CapabilityIds.Temperature, "temperature", StateValue.Number(_temperatureC)),
        new(CapabilityIds.Humidity, "humidity", StateValue.Number(_humidityPct)),
        new(CapabilityIds.Battery, "battery", StateValue.Number(Math.Round(_batteryPct))),
    ];

    public override void Tick(DateTimeOffset now, Random random)
    {
        _temperatureC = Math.Clamp(Math.Round(_temperatureC + ((random.NextDouble() - 0.5) * 0.2), 1), 18, 30);
        _humidityPct = Math.Clamp(Math.Round(_humidityPct + ((random.NextDouble() - 0.5) * 2), 0), 35, 95);
        _batteryPct = Math.Max(0, _batteryPct - 0.001);
    }

    public override void Apply(DeviceCommand command, DateTimeOffset now) => throw Unsupported(command);
}

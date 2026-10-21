using System.Globalization;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;

namespace ControlHome.Connectors.Simulated;

/// <summary>One attribute value of a simulated device.</summary>
internal readonly record struct SimulatedValue(string EndpointKey, string CapabilityId, string AttributeKey, StateValue Value)
{
    /// <summary>A value of the device's main endpoint.</summary>
    public SimulatedValue(string capabilityId, string attributeKey, StateValue value)
        : this(Endpoint.MainKey, capabilityId, attributeKey, value)
    {
    }
}

/// <summary>
/// The simulated home of a preset: its devices in a fixed order and one seeded random generator. Not thread-safe: the
/// runtime serializes every call. Determinism: the same seed and the same sequence of ticks/commands at the same times
/// produce the same values.
/// </summary>
internal sealed class SimulatedHome
{
    private readonly Random _random;

    public SimulatedHome(SimulatedSettings settings)
    {
        Settings = settings;
        _random = new Random(settings.Seed);

        // The basic preset; "demo" adds the rest of the demo home after the same first five devices.
        List<SimulatedDevice> devices =
        [
            new SimulatedLight(settings.Seed, _random),
            new SimulatedPlug(settings.Seed, _random),
            new SimulatedAirConditioner(settings.Seed, _random),
            new SimulatedContactSensor(settings.Seed, _random),
            new SimulatedLock(settings.Seed),
        ];
        if (settings.Preset == SimulatedSettings.DemoPreset)
        {
            devices.AddRange(SimulatedDemoDevices.Create(settings.Seed, _random));
        }

        Devices = devices;
    }

    public SimulatedSettings Settings { get; }

    public IReadOnlyList<SimulatedDevice> Devices { get; }

    public SimulatedDevice? Find(string externalId) =>
        Devices.FirstOrDefault(d => string.Equals(d.ExternalId, externalId, StringComparison.Ordinal));

    public void Start(DateTimeOffset now)
    {
        foreach (var device in Devices)
        {
            device.Start(now);
        }
    }

    public void Tick(DateTimeOffset now)
    {
        foreach (var device in Devices)
        {
            device.Tick(now, _random);
        }
    }
}

/// <summary>
/// A simulated device. Most have one endpoint (<see cref="Endpoint.MainKey"/>) with <see cref="Capabilities"/>; devices
/// with several endpoints (a 3-gang switch) override <see cref="Endpoints"/>.
/// </summary>
internal abstract class SimulatedDevice(string externalId, int index, int seed)
{
    public string ExternalId { get; } = externalId;

    public abstract string Name { get; }

    public abstract string DeviceClass { get; }

    public abstract string Model { get; }

    public abstract string SuggestedRoom { get; }

    /// <summary>The device it is reached through (a camera's NVR), or <c>null</c>.</summary>
    public virtual string? ParentExternalId => null;

    /// <summary>The capabilities of the main endpoint (with the device's constraints).</summary>
    public virtual IReadOnlyList<CapabilityBinding> Capabilities => [];

    /// <summary>The device's endpoints; by default only the main endpoint with <see cref="Capabilities"/>.</summary>
    public virtual IReadOnlyList<DiscoveredEndpoint> Endpoints => [new DiscoveredEndpoint(Endpoint.MainKey, null, Capabilities)];

    public DiscoveredDevice Describe() => new(
        ExternalId,
        Name,
        DeviceClass,
        "ControlHome",
        Model,
        "1.0.0",
        ParentExternalId,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Locally administered MAC derived from the seed: the same seed is the same (duplicate) hardware.
            ["mac"] = string.Create(
                CultureInfo.InvariantCulture,
                $"02:00:{(seed >> 16) & 0xFF:X2}:{(seed >> 8) & 0xFF:X2}:{seed & 0xFF:X2}:{index:X2}"),
            ["serial"] = string.Create(CultureInfo.InvariantCulture, $"SIM-{seed}-{index}"),
        },
        SuggestedRoom,
        Endpoints);

    /// <summary>The current value of every attribute, in a fixed order.</summary>
    public abstract IEnumerable<SimulatedValue> Snapshot();

    public virtual void Start(DateTimeOffset now)
    {
    }

    public virtual void Tick(DateTimeOffset now, Random random)
    {
    }

    /// <summary>Applies an already validated command of one of the device's endpoint capabilities.</summary>
    public abstract void Apply(DeviceCommand command, DateTimeOffset now);

    /// <summary>The binding of a capability on an endpoint, or <c>null</c> when the endpoint does not have it.</summary>
    public CapabilityBinding? BindingOf(string endpointKey, string capabilityId) =>
        Endpoints.FirstOrDefault(e => string.Equals(e.Key, endpointKey, StringComparison.Ordinal))?
            .Capabilities.FirstOrDefault(c => string.Equals(c.CapabilityId, capabilityId, StringComparison.Ordinal));

    /// <summary>The current values of one endpoint capability's attributes, by key (for mode-dependent argument ranges).</summary>
    public IReadOnlyDictionary<string, StateValue> StateOf(string endpointKey, string capabilityId) =>
        Snapshot().Where(v => string.Equals(v.EndpointKey, endpointKey, StringComparison.Ordinal)
                && string.Equals(v.CapabilityId, capabilityId, StringComparison.Ordinal))
            .ToDictionary(v => v.AttributeKey, v => v.Value, StringComparer.Ordinal);

    protected static double Number(DeviceCommand command, string argument) => command.Arguments.GetProperty(argument).GetDouble();

    protected static string Text(DeviceCommand command, string argument) => command.Arguments.GetProperty(argument).GetString()!;

    protected static bool Flag(DeviceCommand command, string argument) => command.Arguments.GetProperty(argument).GetBoolean();

    protected static CapabilityConstraints Constraints(params (string Attribute, AttributeConstraint Constraint)[] attributes) =>
        new(attributes.ToDictionary(a => a.Attribute, a => a.Constraint, StringComparer.Ordinal));

    protected static InvalidOperationException Unsupported(DeviceCommand command) =>
        new($"Unsupported command {command.CapabilityId}.{command.Command}.");
}

/// <summary>A dimmable light: <c>switch</c> + <c>level</c> (level 1–100).</summary>
internal sealed class SimulatedLight : SimulatedDevice
{
    private bool _on = true;
    private double _level;

    public SimulatedLight(int seed, Random random)
        : base("light-1", 1, seed)
    {
        _level = random.Next(20, 101);
    }

    public override string Name => "Living room light";

    public override string DeviceClass => DeviceClasses.Light;

    public override string Model => "Simulated dimmable light";

    public override string SuggestedRoom => "Living room";

    public override IReadOnlyList<CapabilityBinding> Capabilities { get; } =
    [
        CapabilityBinding.Of(CapabilityIds.Switch),
        CapabilityBinding.Of(CapabilityIds.Level, Constraints(("level", new AttributeConstraint(Min: 1)))),
    ];

    public override IEnumerable<SimulatedValue> Snapshot() =>
    [
        new(CapabilityIds.Switch, "on", StateValue.Bool(_on)),
        new(CapabilityIds.Level, "level", StateValue.Number(_level)),
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
            default:
                throw Unsupported(command);
        }
    }
}

/// <summary>
/// A smart plug: <c>switch</c> + <c>power</c> + <c>energy</c>. While on, the power does a random walk (5–2000 W, 0.1 W
/// steps) on every tick; energy integrates the power in effect over (simulated) time, including partial intervals before a
/// switch change.
/// </summary>
internal sealed class SimulatedPlug : SimulatedDevice
{
    private readonly double _basePowerW;
    private bool _on = true;
    private double _powerW;
    private double _energyWh;
    private DateTimeOffset _integratedUntil;

    /// <summary>The basic preset's coffee maker plug.</summary>
    public SimulatedPlug(int seed, Random random)
        : this("plug-1", 2, "Coffee maker plug", "Kitchen", seed, random, minBaseW: 40, spreadW: 80)
    {
    }

    public SimulatedPlug(string externalId, int index, string name, string room, int seed, Random random, double minBaseW, double spreadW)
        : base(externalId, index, seed)
    {
        ArgumentNullException.ThrowIfNull(random);
        Name = name;
        SuggestedRoom = room;
        _basePowerW = Math.Round(minBaseW + (random.NextDouble() * spreadW), 1);
        _powerW = _basePowerW;
        _energyWh = Math.Round(random.NextDouble() * 5000, 3);
    }

    public override string Name { get; }

    public override string DeviceClass => DeviceClasses.Outlet;

    public override string Model => "Simulated smart plug";

    public override string SuggestedRoom { get; }

    public override IReadOnlyList<CapabilityBinding> Capabilities { get; } =
    [
        CapabilityBinding.Of(CapabilityIds.Switch),
        CapabilityBinding.Of(CapabilityIds.Power),
        CapabilityBinding.Of(CapabilityIds.Energy),
    ];

    /// <summary>The exact (unrounded) energy counter, for tests.</summary>
    internal double EnergyWh => _energyWh;

    public override IEnumerable<SimulatedValue> Snapshot() =>
    [
        new(CapabilityIds.Switch, "on", StateValue.Bool(_on)),
        new(CapabilityIds.Power, "power", StateValue.Number(_powerW)),
        new(CapabilityIds.Energy, "energy", StateValue.Number(Math.Round(_energyWh, 3))),
    ];

    public override void Start(DateTimeOffset now) => _integratedUntil = now;

    public override void Tick(DateTimeOffset now, Random random)
    {
        Integrate(now);
        if (_on)
        {
            _powerW = Math.Clamp(Math.Round(_powerW + (random.NextDouble() * 10) - 5, 1), 5, 2000);
        }
    }

    public override void Apply(DeviceCommand command, DateTimeOffset now)
    {
        Integrate(now);
        _on = command.Command switch
        {
            "turnOn" => true,
            "turnOff" => false,
            "toggle" => !_on,
            _ => throw Unsupported(command),
        };
        _powerW = _on ? _basePowerW : 0;
    }

    private void Integrate(DateTimeOffset now)
    {
        if (now > _integratedUntil)
        {
            _energyWh += _powerW * (now - _integratedUntil).TotalHours;
            _integratedUntil = now;
        }
    }
}

/// <summary>
/// A split air conditioner: <c>switch</c> + <c>thermostat</c> + <c>temperature</c>. While on (and not in fan mode) the room
/// temperature moves towards the target by up to 0.1 °C per tick; otherwise it drifts back towards 29 °C.
/// </summary>
internal sealed class SimulatedAirConditioner : SimulatedDevice
{
    private const double AmbientC = 29;
    private const string Off = "off";

    private bool _on = true;
    private string _mode = "cool";
    private string _lastActiveMode = "cool";
    private double _targetC = 24;
    private double _currentC;
    private string _fanMode = "auto";

    public SimulatedAirConditioner(int seed, Random random)
        : base("ac-1", 3, seed)
    {
        _currentC = Math.Round(26 + (random.NextDouble() * 4), 1);
    }

    public override string Name => "Bedroom air conditioner";

    public override string DeviceClass => DeviceClasses.AirConditioner;

    public override string Model => "Simulated split AC";

    public override string SuggestedRoom => "Bedroom";

    public override IReadOnlyList<CapabilityBinding> Capabilities { get; } =
    [
        CapabilityBinding.Of(CapabilityIds.Switch),
        CapabilityBinding.Of(
            CapabilityIds.Thermostat,
            Constraints(
                ("mode", new AttributeConstraint(Values: ["off", "cool", "heat", "auto", "fan"])),
                ("fanMode", new AttributeConstraint(Values: ["auto", "low", "medium", "high"])),
                ("targetTemperature", new AttributeConstraint(
                    Min: 16,
                    Max: 30,
                    Step: 0.5,
                    DependsOn: "mode",
                    Cases: new Dictionary<string, AttributeConstraint> { ["heat"] = new(Max: 26) })))),
        CapabilityBinding.Of(CapabilityIds.Temperature),
    ];

    public override IEnumerable<SimulatedValue> Snapshot() =>
    [
        new(CapabilityIds.Switch, "on", StateValue.Bool(_on)),
        new(CapabilityIds.Thermostat, "mode", StateValue.String(_mode)),
        new(CapabilityIds.Thermostat, "targetTemperature", StateValue.Number(_targetC)),
        new(CapabilityIds.Thermostat, "currentTemperature", StateValue.Number(_currentC)),
        new(CapabilityIds.Thermostat, "fanMode", StateValue.String(_fanMode)),
        new(CapabilityIds.Temperature, "temperature", StateValue.Number(_currentC)),
    ];

    public override void Tick(DateTimeOffset now, Random random)
    {
        var conditioning = _on && _mode != "fan";
        var goal = conditioning ? _targetC : AmbientC;
        var maxStep = conditioning ? 0.1 : 0.05;
        _currentC = Math.Round(_currentC + Math.Clamp(goal - _currentC, -maxStep, maxStep), 2);
    }

    public override void Apply(DeviceCommand command, DateTimeOffset now)
    {
        switch (command.Command)
        {
            case "turnOn":
                SetOn(true);
                break;
            case "turnOff":
                SetOn(false);
                break;
            case "toggle":
                SetOn(!_on);
                break;
            case "setMode":
                var mode = Text(command, "mode");
                if (mode == Off)
                {
                    SetOn(false);
                }
                else
                {
                    _on = true;
                    _mode = _lastActiveMode = mode;
                }

                break;
            case "setTargetTemperature":
                _targetC = Number(command, "targetTemperature");
                break;
            case "setFanMode":
                _fanMode = Text(command, "fanMode");
                break;
            default:
                throw Unsupported(command);
        }
    }

    private void SetOn(bool on)
    {
        _on = on;
        _mode = on ? _lastActiveMode : Off;
    }
}

/// <summary>A door contact sensor: <c>contact</c> + <c>battery</c>. Opens/closes now and then; the battery drains slowly.</summary>
internal sealed class SimulatedContactSensor : SimulatedDevice
{
    private bool _open;
    private double _batteryPct;

    public SimulatedContactSensor(int seed, Random random)
        : base("contact-1", 4, seed)
    {
        _batteryPct = random.Next(70, 101);
    }

    public override string Name => "Front door sensor";

    public override string DeviceClass => DeviceClasses.Door;

    public override string Model => "Simulated door contact";

    public override string SuggestedRoom => "Entrance";

    public override IReadOnlyList<CapabilityBinding> Capabilities { get; } =
    [
        CapabilityBinding.Of(CapabilityIds.Contact),
        CapabilityBinding.Of(CapabilityIds.Battery),
    ];

    public override IEnumerable<SimulatedValue> Snapshot() =>
    [
        new(CapabilityIds.Contact, "open", StateValue.Bool(_open)),
        new(CapabilityIds.Battery, "battery", StateValue.Number(Math.Round(_batteryPct))),
    ];

    public override void Tick(DateTimeOffset now, Random random)
    {
        if (random.NextDouble() < 0.02)
        {
            _open = !_open;
        }

        _batteryPct = Math.Max(0, _batteryPct - 0.002);
    }

    public override void Apply(DeviceCommand command, DateTimeOffset now) => throw Unsupported(command);
}

/// <summary>A smart lock: <c>lock</c> (unlocking is safety-critical).</summary>
internal sealed class SimulatedLock(int seed) : SimulatedDevice("lock-1", 5, seed)
{
    private string _lockState = "locked";

    public override string Name => "Front door lock";

    public override string DeviceClass => DeviceClasses.Lock;

    public override string Model => "Simulated smart lock";

    public override string SuggestedRoom => "Entrance";

    public override IReadOnlyList<CapabilityBinding> Capabilities { get; } = [CapabilityBinding.Of(CapabilityIds.Lock)];

    public override IEnumerable<SimulatedValue> Snapshot() => [new(CapabilityIds.Lock, "lockState", StateValue.String(_lockState))];

    public override void Apply(DeviceCommand command, DateTimeOffset now) =>
        _lockState = command.Command switch
        {
            "lock" => "locked",
            "unlock" => "unlocked",
            _ => throw Unsupported(command),
        };
}

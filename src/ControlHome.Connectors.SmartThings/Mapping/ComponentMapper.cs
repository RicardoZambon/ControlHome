using System.Text.Json;
using ControlHome.Connectors.SmartThings.Model;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;

namespace ControlHome.Connectors.SmartThings.Mapping;

/// <summary>Maps one component to one endpoint (see <see cref="SmartThingsDeviceMapper"/>).</summary>
internal sealed partial class ComponentMapper
{
    private static readonly string[] _mediaCategories = ["Television", "TV", "Speaker", "NetworkAudio", "Receiver", "Soundbar"];

    private readonly SmartThingsDevice _device;
    private readonly DeviceComponent _component;
    private readonly DeviceStatus _status;
    private readonly MapContext _context;
    private readonly HashSet<string> _available;
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);
    private readonly List<MappedReading> _readings = [];
    private readonly List<CapabilityRef> _missing = [];
    private readonly List<ButtonObservation> _buttons = [];
    private readonly List<PowerConsumptionWindow> _windows = [];

    public ComponentMapper(SmartThingsDevice device, DeviceComponent component, DeviceStatus status, MapContext context, string key, bool isMain)
    {
        _device = device;
        _component = component;
        _status = status;
        _context = context;
        Builder = new EndpointBuilder(key, isMain ? null : Naming.Limit(component.Label ?? component.Id, DeviceLimits.NameMaxLength));
        Plan = new EndpointPlan(component.Id);
        var disabled = Json.Strings(Attr(StCapabilities.DisabledCapabilities, "disabledCapabilities")?.Value).ToHashSet(StringComparer.Ordinal);
        _available = component.Capabilities
            .Select(c => c.Id)
            .Where(id => !disabled.Contains(id) && !StCapabilities.Ignored.Contains(id))
            .ToHashSet(StringComparer.Ordinal);
    }

    public EndpointBuilder Builder { get; }

    public EndpointPlan Plan { get; }

    public IReadOnlyList<MappedReading> Readings => _readings;

    public IReadOnlyList<CapabilityRef> Missing => _missing;

    public IReadOnlyList<ButtonObservation> Buttons => _buttons;

    public IReadOnlyList<PowerConsumptionWindow> Windows => _windows;

    /// <summary><c>garage</c> or <c>blind</c> when the component is a cover.</summary>
    public string? CoverClass { get; private set; }

    /// <summary>A device class implied by the capabilities (when the category gives none).</summary>
    public string? CapabilityClass { get; private set; }

    private string Key => Builder.Key;

    public void Run()
    {
        // Only setpoint limits: read by the thermostat (and covered by the setpoint ranges), never an attribute of its own.
        Consume(StCapabilities.ThermostatSetpointControl);
        MapThermostat();
        MapMedia();
        MapAppliance();
        MapCover();
        MapLighting();
        MapLock();
        MapSensors();
        MapButton();
        MapEnergy();
        MapGeneric();
    }

    // ---- climate ------------------------------------------------------------------------------------------------------

    private void MapThermostat()
    {
        var modeCapability = Has(StCapabilities.AirConditionerMode) ? StCapabilities.AirConditionerMode
            : Has(StCapabilities.ThermostatMode) ? StCapabilities.ThermostatMode
            : null;
        if (modeCapability is null)
        {
            return;
        }

        var isAc = modeCapability == StCapabilities.AirConditionerMode;
        var switchTurnsOff = isAc && Has(StCapabilities.Switch);
        Consume(modeCapability, StCapabilities.ThermostatCoolingSetpoint, StCapabilities.ThermostatHeatingSetpoint,
            StCapabilities.ThermostatSetpointControl, StCapabilities.TemperatureMeasurement, StCapabilities.AirConditionerFanMode);
        Builder.Add(CapabilityIds.Thermostat);

        // Modes: what the device supports, as canonical modes (the switch provides "off" for air conditioners).
        var vendorModes = isAc
            ? Json.Strings(Attr(modeCapability, "supportedAcModes")?.Value).Concat(Json.Strings(Attr(modeCapability, "availableAcModes")?.Value))
            : Json.Strings(Attr(modeCapability, "supportedThermostatModes")?.Value);
        var modeMap = ReverseMap(vendorModes.Distinct(StringComparer.Ordinal), CanonicalMode);
        if (modeMap.Count == 0)
        {
            modeMap = ReverseMap(isAc ? ["cool", "dry", "wind", "auto", "heat"] : ["off", "cool", "heat", "auto"], CanonicalMode);
        }

        if (switchTurnsOff)
        {
            modeMap.Remove("off");
        }

        var modes = CatalogOrder(CapabilityCatalog.Thermostat, "mode", [.. modeMap.Keys, .. switchTurnsOff ? ["off"] : Array.Empty<string>()]);
        Builder.Constrain(CapabilityIds.Thermostat, "mode", new AttributeConstraint(Values: modes));

        // Setpoints: the cooling setpoint is the AC's only one; thermostats heat to the heating setpoint.
        var cooling = Has(StCapabilities.ThermostatCoolingSetpoint);
        var heating = Has(StCapabilities.ThermostatHeatingSetpoint);
        var setpoint = Attr(StCapabilities.ThermostatCoolingSetpoint, "coolingSetpoint")
            ?? Attr(StCapabilities.ThermostatHeatingSetpoint, "heatingSetpoint");
        var unit = setpoint?.Unit ?? Attr(StCapabilities.TemperatureMeasurement, "temperature")?.Unit ?? "C";
        var conversion = StUnits.Canonical(unit).Conversion;
        var (min, max, step) = SetpointRange(conversion);
        if (min is not null || max is not null)
        {
            Builder.Constrain(CapabilityIds.Thermostat, "targetTemperature", new AttributeConstraint(Min: min, Max: max));
        }

        // Fan.
        var fanMap = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Has(StCapabilities.AirConditionerFanMode))
        {
            var vendorFans = Json.Strings(Attr(StCapabilities.AirConditionerFanMode, "supportedAcFanModes")?.Value);
            fanMap = ReverseMap(vendorFans.Count > 0 ? vendorFans : ["auto", "low", "medium", "high", "turbo"], CanonicalFan);
            var fans = CatalogOrder(CapabilityCatalog.Thermostat, "fanMode", [.. fanMap.Keys]);
            if (fans.Count > 0)
            {
                Builder.Constrain(CapabilityIds.Thermostat, "fanMode", new AttributeConstraint(Values: fans));
            }
        }

        Plan.Thermostat = new ThermostatPlan(modeCapability, modeMap, switchTurnsOff, cooling, heating, conversion, step, fanMap);

        // Readings.
        var modeState = Attr(modeCapability, isAc ? "airConditionerMode" : "thermostatMode");
        var switchState = switchTurnsOff ? Attr(StCapabilities.Switch, "switch") : null;
        string? mode = switchState?.Text == "off" ? "off" : modeState?.Text is { } vendor ? CanonicalMode(vendor) : null;
        Report(CapabilityIds.Thermostat, "mode", Text(mode), Latest(modeState, switchState));

        var target = isAc || mode != "heat"
            ? Attr(StCapabilities.ThermostatCoolingSetpoint, "coolingSetpoint") ?? Attr(StCapabilities.ThermostatHeatingSetpoint, "heatingSetpoint")
            : Attr(StCapabilities.ThermostatHeatingSetpoint, "heatingSetpoint") ?? Attr(StCapabilities.ThermostatCoolingSetpoint, "coolingSetpoint");
        Report(
            CapabilityIds.Thermostat,
            "targetTemperature",
            target?.Number is { } value ? StateValue.Number(StUnits.Snap(StUnits.Celsius(value, target.Unit ?? unit), 0.5)) : StateValue.Null,
            target?.Timestamp);

        var current = Attr(StCapabilities.TemperatureMeasurement, "temperature");
        Report(CapabilityIds.Thermostat, "currentTemperature", Celsius(current), current?.Timestamp);

        if (Has(StCapabilities.AirConditionerFanMode))
        {
            var fan = Attr(StCapabilities.AirConditionerFanMode, "fanMode");
            Report(CapabilityIds.Thermostat, "fanMode", Text(fan?.Text is { } vendorFan ? CanonicalFan(vendorFan) : null), fan?.Timestamp);
        }

        CapabilityClass ??= isAc ? DeviceClasses.AirConditioner : DeviceClasses.Thermostat;
    }

    private (double? Min, double? Max, double? Step) SetpointRange(UnitConversion conversion)
    {
        double? min = null;
        double? max = null;
        double? step = null;
        if (Attr(StCapabilities.ThermostatSetpointControl, "minimumSetpoint") is { Number: { } low } minimum
            && Attr(StCapabilities.ThermostatSetpointControl, "maximumSetpoint") is { Number: { } high } maximum)
        {
            min = StUnits.Celsius(low, minimum.Unit);
            max = StUnits.Celsius(high, maximum.Unit);
        }

        var range = Attr(StCapabilities.ThermostatCoolingSetpoint, "coolingSetpointRange")
            ?? Attr(StCapabilities.ThermostatHeatingSetpoint, "heatingSetpointRange");
        if (range is { HasValue: true, Value.ValueKind: JsonValueKind.Object })
        {
            var rangeConversion = range.Unit is null ? conversion : StUnits.Canonical(range.Unit).Conversion;
            if (min is null && range.Value.Num("minimum") is { } low2 && range.Value.Num("maximum") is { } high2)
            {
                min = StUnits.ToCanonical(low2, rangeConversion);
                max = StUnits.ToCanonical(high2, rangeConversion);
            }

            step = range.Value.Num("step") is { } s and > 0 ? s : null;
        }

        // Only ever narrows the catalogue range (5–35 °C, 0.5 steps).
        min = min is { } a ? Math.Clamp(Math.Ceiling(a * 2) / 2, 5, 35) : null;
        max = max is { } b ? Math.Clamp(Math.Floor(b * 2) / 2, 5, 35) : null;
        return min > max ? (null, null, step) : (min, max, step);
    }

    private static string? CanonicalMode(string vendor) => vendor switch
    {
        "off" => "off",
        "cool" or "coolClean" => "cool",
        "heat" or "heatClean" or "emergency heat" => "heat",
        "auto" => "auto",
        "dry" or "dryClean" => "dry",
        "wind" or "fan" or "fanOnly" => "fan",
        _ => null,
    };

    private static string? CanonicalFan(string vendor) => vendor switch
    {
        "auto" => "auto",
        "quiet" or "sleep" => "quiet",
        "low" => "low",
        "medium" or "mid" => "medium",
        "high" => "high",
        "turbo" => "turbo",
        _ => null,
    };

    // ---- media --------------------------------------------------------------------------------------------------------

    private void MapMedia()
    {
        var category = _component.Categories.Select(c => c.Name).Concat(_device.Main?.Categories.Select(c => c.Name) ?? []);
        var isMedia = Has(StCapabilities.MediaPlayback) || Has(StCapabilities.MediaInputSource) || Has(StCapabilities.SamsungMediaInputSource)
            || Has(StCapabilities.TvChannel) || (Has(StCapabilities.AudioVolume) && category.Any(c => _mediaCategories.Contains(c, StringComparer.Ordinal)));
        if (!isMedia)
        {
            return;
        }

        var input = Has(StCapabilities.MediaInputSource) ? StCapabilities.MediaInputSource
            : Has(StCapabilities.SamsungMediaInputSource) ? StCapabilities.SamsungMediaInputSource
            : null;
        Plan.Media = new MediaPlan(
            Has(StCapabilities.Switch),
            Has(StCapabilities.AudioVolume),
            Has(StCapabilities.AudioMute),
            input,
            Has(StCapabilities.MediaPlayback),
            Has(StCapabilities.MediaTrackControl),
            _component.Has(StCapabilities.LaunchApp));
        Consume(StCapabilities.MediaPlayback, StCapabilities.AudioVolume, StCapabilities.AudioMute, StCapabilities.MediaInputSource,
            StCapabilities.SamsungMediaInputSource, StCapabilities.MediaTrackControl, StCapabilities.Switch, StCapabilities.LaunchApp);
        Builder.Add(CapabilityIds.MediaPlayer);

        if (Plan.Media.Switch)
        {
            var power = Attr(StCapabilities.Switch, "switch");
            Report(CapabilityIds.MediaPlayer, "power", OnOff(power), power?.Timestamp);
        }

        if (Plan.Media.Volume)
        {
            var volume = Attr(StCapabilities.AudioVolume, "volume");
            Report(CapabilityIds.MediaPlayer, "volume", Percent(volume), volume?.Timestamp);
        }

        if (Plan.Media.Mute)
        {
            var mute = Attr(StCapabilities.AudioMute, "mute");
            Report(
                CapabilityIds.MediaPlayer,
                "muted",
                mute?.Text switch { "muted" => StateValue.True, "unmuted" => StateValue.False, _ => StateValue.Null },
                mute?.Timestamp);
        }

        if (input is not null)
        {
            var source = Attr(input, "inputSource");
            IReadOnlyList<string> supported = input == StCapabilities.MediaInputSource
                ? Json.Strings(Attr(input, "supportedInputSources")?.Value)
                : [.. Attr(input, "supportedInputSourcesMap") is { Value.ValueKind: JsonValueKind.Array } map
                    ? map.Value.EnumerateArray().Select(i => i.Str("id")).OfType<string>()
                    : []];
            var values = supported.Concat(source?.Text is { Length: > 0 } now ? [now] : []).Distinct(StringComparer.Ordinal).ToList();
            if (values.Count > 0)
            {
                Builder.Constrain(CapabilityIds.MediaPlayer, "source", new AttributeConstraint(Values: values));
            }

            Report(CapabilityIds.MediaPlayer, "source", source?.Text is { Length: > 0 } text ? Text(Naming.Limit(text, 128)) : StateValue.Null, source?.Timestamp);
        }

        if (Plan.Media.Playback)
        {
            var playback = Attr(StCapabilities.MediaPlayback, "playbackStatus");
            Report(
                CapabilityIds.MediaPlayer,
                "playbackState",
                Text(playback?.Text switch
                {
                    "playing" or "fast forwarding" or "rewinding" => "playing",
                    "paused" => "paused",
                    "stopped" => "stopped",
                    "buffering" => "buffering",
                    null => null,
                    _ => "unknown",
                }),
                playback?.Timestamp);
        }

        CapabilityClass ??= DeviceClasses.Tv;
    }

    // ---- appliances ---------------------------------------------------------------------------------------------------

    private void MapAppliance()
    {
        string[] standard = [StCapabilities.WasherOperatingState, StCapabilities.DryerOperatingState, StCapabilities.DishwasherOperatingState];
        string[] samsung = [StCapabilities.SamsungWasherOperatingState, StCapabilities.SamsungDryerOperatingState, StCapabilities.SamsungDishwasherOperatingState];
        var main = standard.FirstOrDefault(Has);
        var extra = samsung.FirstOrDefault(Has);
        if (main is null && extra is null)
        {
            return;
        }

        Consume([.. standard, .. samsung, StCapabilities.RemoteControlStatus]);
        Builder.Add(CapabilityIds.ApplianceCycle);
        Plan.Appliance = main is not null ? new AppliancePlan(main, SetMachineState: true) : new AppliancePlan(extra!, SetMachineState: false);
        var kind = (main ?? extra!) switch
        {
            var c when c.Contains("washer", StringComparison.Ordinal) && !c.Contains("dishwasher", StringComparison.Ordinal) => "washer",
            var c when c.Contains("dryer", StringComparison.Ordinal) => "dryer",
            _ => "dishwasher",
        };

        var machine = main is not null ? Attr(main, "machineState") : null;
        var operating = extra is not null ? Attr(extra, "operatingState") : null;
        var job = (main is not null ? Attr(main, kind + "JobState") : null) ?? (extra is not null ? Attr(extra, kind + "JobState") : null);
        var running = machine?.Text switch
        {
            "run" => "running",
            "pause" => "paused",
            "stop" => "idle",
            _ => operating?.Text switch
            {
                "running" => "running",
                "paused" => "paused",
                "ready" => "idle",
                _ => null,
            },
        };
        if (running == "idle" && job?.Text is "finish" or "finished")
        {
            running = "finished";
        }

        Report(CapabilityIds.ApplianceCycle, "machineState", Text(running), Latest(machine, operating));
        Report(CapabilityIds.ApplianceCycle, "phase", job?.Text is { Length: > 0 } phase && phase != "none" ? Text(Naming.Limit(phase, 64)) : StateValue.Null, job?.Timestamp);

        StateValue remaining;
        var remainingTime = extra is not null ? Attr(extra, "remainingTime") : null;
        if (running is not ("running" or "paused"))
        {
            remaining = running is null ? StateValue.Null : StateValue.Number(0);
        }
        else if (remainingTime?.Number is { } minutes and >= 0)
        {
            remaining = StateValue.Number(Math.Round(remainingTime.Unit is "s" ? minutes / 60 : minutes));
        }
        else if (main is not null && Attr(main, "completionTime") is { HasValue: true } completion
            && DateTimeOffset.TryParse(completion.Text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var end))
        {
            remaining = StateValue.Number(Math.Max(0, Math.Ceiling((end - _context.NowUtc).TotalMinutes)));
        }
        else
        {
            remaining = StateValue.Null;
        }

        Report(CapabilityIds.ApplianceCycle, "remainingMinutes", remaining, null);

        // Many Samsung appliances accept remote commands only while "remote control" is enabled on the machine itself.
        var remote = Attr(StCapabilities.RemoteControlStatus, "remoteControlEnabled");
        var enabled = _component.Has(StCapabilities.RemoteControlStatus)
            ? remote is { HasValue: true } value ? Json.AsBool(value.Value) is { } flag ? StateValue.Bool(flag) : StateValue.Null : StateValue.Null
            : StateValue.True;
        Report(CapabilityIds.ApplianceCycle, "remoteControlEnabled", enabled, remote?.Timestamp);
        CapabilityClass ??= kind;
    }

    // ---- covers -------------------------------------------------------------------------------------------------------

    private void MapCover()
    {
        string? state = Has(StCapabilities.WindowShade) ? StCapabilities.WindowShade
            : Has(StCapabilities.GarageDoorControl) ? StCapabilities.GarageDoorControl
            : Has(StCapabilities.DoorControl) ? StCapabilities.DoorControl
            : null;
        if (state is null)
        {
            return;
        }

        var shade = state == StCapabilities.WindowShade;
        var position = shade ? Has(StCapabilities.WindowShadeLevel) ? StCapabilities.WindowShadeLevel : Has(StCapabilities.SwitchLevel) ? StCapabilities.SwitchLevel : null : null;
        Consume(StCapabilities.WindowShade, StCapabilities.WindowShadeLevel, StCapabilities.GarageDoorControl, StCapabilities.DoorControl);
        if (position is not null)
        {
            Consume(position);
        }

        Builder.Add(CapabilityIds.Cover);
        Plan.Cover = new CoverPlan(state, CanPause: shade, position);
        var value = Attr(state, shade ? "windowShade" : "door");
        Report(
            CapabilityIds.Cover,
            "coverState",
            Text(value?.Text switch
            {
                "open" => "open",
                "closed" => "closed",
                "opening" => "opening",
                "closing" => "closing",
                "partially open" => "stopped",
                null => null,
                _ => "unknown",
            }),
            value?.Timestamp);
        if (position is not null)
        {
            var level = Attr(position, position == StCapabilities.WindowShadeLevel ? "shadeLevel" : "level");
            Report(CapabilityIds.Cover, "position", Percent(level), level?.Timestamp);
        }

        CoverClass = shade ? DeviceClasses.Blind : DeviceClasses.Garage;
    }

    // ---- lights and switches ------------------------------------------------------------------------------------------

    private void MapLighting()
    {
        if (Has(StCapabilities.Switch) && !_consumed.Contains(StCapabilities.Switch))
        {
            Consume(StCapabilities.Switch);
            Builder.Add(CapabilityIds.Switch);
            Plan.Switch = true;
            var on = Attr(StCapabilities.Switch, "switch");
            Report(CapabilityIds.Switch, "on", OnOff(on), on?.Timestamp);
        }

        if (Has(StCapabilities.SwitchLevel) && !_consumed.Contains(StCapabilities.SwitchLevel))
        {
            Consume(StCapabilities.SwitchLevel);
            Builder.Add(CapabilityIds.Level);
            Plan.Level = true;
            if (Attr(StCapabilities.SwitchLevel, "levelRange") is { Value.ValueKind: JsonValueKind.Object } range
                && range.Value.Num("minimum") is { } low && range.Value.Num("maximum") is { } high && low <= high)
            {
                Builder.Constrain(CapabilityIds.Level, "level", new AttributeConstraint(Min: Math.Clamp(Math.Ceiling(low), 0, 100), Max: Math.Clamp(Math.Floor(high), 0, 100)));
            }

            var level = Attr(StCapabilities.SwitchLevel, "level");
            Report(CapabilityIds.Level, "level", Percent(level), level?.Timestamp);
        }

        var hue = Has(StCapabilities.ColorControl);
        var temperature = Has(StCapabilities.ColorTemperature);
        if (!hue && !temperature)
        {
            return;
        }

        Consume(StCapabilities.ColorControl, StCapabilities.ColorTemperature);
        Builder.Add(CapabilityIds.Color);
        Plan.ColorHue = hue;
        Plan.ColorTemperature = temperature;
        if (hue)
        {
            // SmartThings hue is 0–100 (percent of the circle); the catalogue's is degrees.
            var h = Attr(StCapabilities.ColorControl, "hue");
            Report(CapabilityIds.Color, "hue", h?.Number is { } value and >= 0 and <= 100 ? StateValue.Number(Math.Round(value * 3.6, 2)) : StateValue.Null, h?.Timestamp);
            var saturation = Attr(StCapabilities.ColorControl, "saturation");
            Report(CapabilityIds.Color, "saturation", Percent(saturation), saturation?.Timestamp);
        }

        if (temperature)
        {
            if (Attr(StCapabilities.ColorTemperature, "colorTemperatureRange") is { Value.ValueKind: JsonValueKind.Object } range
                && range.Value.Num("minimum") is { } low && range.Value.Num("maximum") is { } high && low <= high)
            {
                Builder.Constrain(
                    CapabilityIds.Color,
                    "colorTemperature",
                    new AttributeConstraint(Min: Math.Clamp(Math.Ceiling(low), 1000, 10_000), Max: Math.Clamp(Math.Floor(high), 1000, 10_000)));
            }

            var kelvin = Attr(StCapabilities.ColorTemperature, "colorTemperature");
            Report(
                CapabilityIds.Color,
                "colorTemperature",
                kelvin?.Number is { } k and >= 1000 and <= 10_000 ? StateValue.Number(Math.Round(k)) : StateValue.Null,
                kelvin?.Timestamp);
        }

        CapabilityClass ??= DeviceClasses.Light;
    }

    private void MapLock()
    {
        if (!Has(StCapabilities.Lock))
        {
            return;
        }

        Consume(StCapabilities.Lock);
        Builder.Add(CapabilityIds.Lock);
        Plan.Lock = true;
        var state = Attr(StCapabilities.Lock, "lock");
        Report(
            CapabilityIds.Lock,
            "lockState",
            Text(state?.Text switch
            {
                "locked" => "locked",
                "unlocked" or "unlocked with timeout" => "unlocked",
                "jammed" => "jammed",
                null => null,
                _ => "unknown",
            }),
            state?.Timestamp);
        CapabilityClass ??= DeviceClasses.Lock;
    }

    // ---- sensors ------------------------------------------------------------------------------------------------------

    private void MapSensors()
    {
        if (Take(StCapabilities.TemperatureMeasurement))
        {
            var temperature = Attr(StCapabilities.TemperatureMeasurement, "temperature");
            Sensor(CapabilityIds.Temperature, "temperature", Celsius(temperature), temperature);
        }

        if (Take(StCapabilities.RelativeHumidityMeasurement))
        {
            var humidity = Attr(StCapabilities.RelativeHumidityMeasurement, "humidity");
            Sensor(CapabilityIds.Humidity, "humidity", humidity?.Number is { } h and >= 0 and <= 100 ? StateValue.Number(h) : StateValue.Null, humidity);
        }

        Binary(StCapabilities.ContactSensor, "contact", CapabilityIds.Contact, "open", "open", "closed");
        Binary(StCapabilities.MotionSensor, "motion", CapabilityIds.Motion, "motion", "active", "inactive");
        Binary(StCapabilities.PresenceSensor, "presence", CapabilityIds.Presence, "present", "present", "not present");
        Binary(StCapabilities.OccupancySensor, "occupancy", CapabilityIds.Occupancy, "occupied", "occupied", "unoccupied");
        Binary(StCapabilities.WaterSensor, "water", CapabilityIds.WaterLeak, "leak", "wet", "dry");
        if (Take(StCapabilities.SmokeDetector))
        {
            var smoke = Attr(StCapabilities.SmokeDetector, "smoke");
            Sensor(CapabilityIds.Smoke, "smoke", smoke?.Text switch { "detected" => StateValue.True, "clear" or "tested" => StateValue.False, _ => StateValue.Null }, smoke);
        }

        if (Take(StCapabilities.Battery))
        {
            var battery = Attr(StCapabilities.Battery, "battery");
            Sensor(CapabilityIds.Battery, "battery", Percent(battery), battery);
        }

        // Air quality: PM2.5, CO2 and VOC have catalogue attributes; PM10 (dustLevel) stays generic.
        var fine = Has(StCapabilities.FineDustSensor) ? Attr(StCapabilities.FineDustSensor, "fineDustLevel")
            : Has(StCapabilities.DustSensor) ? Attr(StCapabilities.DustSensor, "fineDustLevel")
            : null;
        var co2 = Has(StCapabilities.CarbonDioxideMeasurement) ? Attr(StCapabilities.CarbonDioxideMeasurement, "carbonDioxide") : null;
        var voc = Has(StCapabilities.TvocMeasurement) ? Attr(StCapabilities.TvocMeasurement, "tvocLevel") : null;
        if (Has(StCapabilities.FineDustSensor) || Has(StCapabilities.DustSensor) || Has(StCapabilities.CarbonDioxideMeasurement) || Has(StCapabilities.TvocMeasurement))
        {
            Builder.Add(CapabilityIds.AirQuality);
            if (Take(StCapabilities.FineDustSensor) | Has(StCapabilities.DustSensor))
            {
                Report(CapabilityIds.AirQuality, "pm25", fine?.Number is { } pm and >= 0 ? StateValue.Number(pm) : StateValue.Null, fine?.Timestamp);
            }

            if (Take(StCapabilities.CarbonDioxideMeasurement))
            {
                Report(CapabilityIds.AirQuality, "co2", co2?.Number is { } ppm and >= 0 ? StateValue.Number(ppm) : StateValue.Null, co2?.Timestamp);
            }

            if (Take(StCapabilities.TvocMeasurement))
            {
                var ppb = voc?.Number is { } level and >= 0 ? voc.Unit == "ppm" ? level * 1000 : level : (double?)null;
                Report(CapabilityIds.AirQuality, "voc", ppb is { } v ? StateValue.Number(v) : StateValue.Null, voc?.Timestamp);
            }
        }

        if (Take(StCapabilities.DustSensor))
        {
            var pm10 = Attr(StCapabilities.DustSensor, "dustLevel");
            var key = Builder.AddGenericAttribute("dustLevel", new NumberSchema(0, null, null, Units.MicrogramsPerCubicMeter), writable: false);
            Report(CapabilityIds.Generic, key, pm10?.Number is { } value and >= 0 ? StateValue.Number(value) : StateValue.Null, pm10?.Timestamp);
        }
    }

    private void Binary(string capability, string attribute, string target, string targetAttribute, string trueValue, string falseValue)
    {
        if (!Take(capability))
        {
            return;
        }

        var state = Attr(capability, attribute);
        var value = state?.Text is { } text
            ? text == trueValue ? StateValue.True : text == falseValue ? StateValue.False : StateValue.Null
            : StateValue.Null;
        Sensor(target, targetAttribute, value, state);
    }

    private void Sensor(string capability, string attribute, StateValue value, StAttribute? state)
    {
        Builder.Add(capability);
        Report(capability, attribute, value, state?.Timestamp);
        CapabilityClass ??= DeviceClasses.Sensor;
    }

    private void MapButton()
    {
        if (!Take(StCapabilities.Button))
        {
            return;
        }

        Builder.Add(CapabilityIds.Button);
        if (Attr(StCapabilities.Button, "button") is { Timestamp: { } timestamp } press
            && press.Text switch
            {
                "pushed" => "single",
                "double" or "pushed_2x" => "double",
                "held" or "down_hold" or "up_hold" => "long",
                _ => null,
            } is { } name)
        {
            _buttons.Add(new ButtonObservation(Key, name, Clamp(timestamp)));
        }

        CapabilityClass ??= DeviceClasses.Button;
    }

    // ---- energy -------------------------------------------------------------------------------------------------------

    private void MapEnergy()
    {
        var meter = Take(StCapabilities.PowerMeter);
        var counter = Take(StCapabilities.EnergyMeter);
        var report = Take(StCapabilities.PowerConsumptionReport);
        var consumption = report ? Attr(StCapabilities.PowerConsumptionReport, "powerConsumption") : null;
        if (meter)
        {
            var power = Attr(StCapabilities.PowerMeter, "power");
            Builder.Add(CapabilityIds.Power);
            Report(CapabilityIds.Power, "power", Canonical(power, "W"), power?.Timestamp);
        }

        if (counter)
        {
            var energy = Attr(StCapabilities.EnergyMeter, "energy");
            Builder.Add(CapabilityIds.Energy);
            Report(CapabilityIds.Energy, "energy", Canonical(energy, "kWh") is { Kind: StateValueKind.Number, N: >= 0 } wh ? wh : StateValue.Null, energy?.Timestamp);
        }

        if (consumption is not { HasValue: true, Value.ValueKind: JsonValueKind.Object } value)
        {
            return;
        }

        if (PowerConsumptionReport.Parse(value.Value, Key, _device.PresentationId) is { } window)
        {
            _windows.Add(window);
        }

        // The report's cumulative counter (Wh) and power (W) stand in for the meters when the device has none.
        if (!counter && PowerConsumptionReport.Energy(value.Value) is { } total)
        {
            Builder.Add(CapabilityIds.Energy);
            Report(CapabilityIds.Energy, "energy", StateValue.Number(total), value.Timestamp);
        }

        if (!meter && PowerConsumptionReport.Power(value.Value, _device.PresentationId) is { } watts)
        {
            Builder.Add(CapabilityIds.Power);
            Report(CapabilityIds.Power, "power", StateValue.Number(watts), value.Timestamp);
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private bool Has(string capability) => _available.Contains(capability);

    /// <summary>Claims a capability that is available and not yet claimed.</summary>
    private bool Take(string capability) => Has(capability) && _consumed.Add(capability);

    private void Consume(params string[] capabilities) => _consumed.UnionWith(capabilities);

    private StAttribute? Attr(string capability, string attribute) => _status.Get(_component.Id, capability, attribute);

    private void Report(string capability, string attribute, StateValue value, DateTimeOffset? observedAt) =>
        _readings.Add(new MappedReading(Key, capability, attribute, value, observedAt is { } at ? Clamp(at) : null));

    private DateTimeOffset Clamp(DateTimeOffset value) => value > _context.NowUtc ? _context.NowUtc : value;

    private static DateTimeOffset? Latest(StAttribute? a, StAttribute? b) =>
        (a?.Timestamp, b?.Timestamp) switch
        {
            ({ } x, { } y) => x > y ? x : y,
            ({ } x, null) => x,
            (null, var y) => y,
        };

    private static StateValue Text(string? value) => value is null ? StateValue.Null : StateValue.String(value);

    private static StateValue OnOff(StAttribute? state) =>
        state?.Text switch { "on" => StateValue.True, "off" => StateValue.False, _ => StateValue.Null };

    private static StateValue Percent(StAttribute? state) =>
        state?.Number is { } value && value >= 0 && value <= 100 ? StateValue.Number(Math.Round(value)) : StateValue.Null;

    private static StateValue Celsius(StAttribute? state) =>
        state?.Number is { } value ? StateValue.Number(StUnits.Celsius(value, state.Unit)) : StateValue.Null;

    /// <summary>A power (W) or energy (Wh) value from its unit, <paramref name="defaultUnit"/> when the device names none.</summary>
    private static StateValue Canonical(StAttribute? state, string defaultUnit) =>
        state?.Number is { } value ? StateValue.Number(StUnits.ToCanonical(value, StUnits.Canonical(state.Unit ?? defaultUnit).Conversion)) : StateValue.Null;

    /// <summary>Canonical value → vendor value, preferring a vendor value spelled like the canonical one.</summary>
    private static Dictionary<string, string> ReverseMap(IEnumerable<string> vendorValues, Func<string, string?> canonical)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var values = vendorValues.ToList();
        foreach (var vendor in values.Where(v => canonical(v) == v))
        {
            map.TryAdd(vendor, vendor);
        }

        foreach (var vendor in values)
        {
            if (canonical(vendor) is { } value)
            {
                map.TryAdd(value, vendor);
            }
        }

        return map;
    }

    /// <summary><paramref name="values"/> in the order of the catalogue attribute's enum.</summary>
    private static List<string> CatalogOrder(CapabilityDefinition capability, string attribute, IReadOnlyCollection<string> values) =>
        capability.FindAttribute(attribute)?.Schema is EnumSchema schema
            ? [.. schema.Values.Where(values.Contains)]
            : [.. values];
}

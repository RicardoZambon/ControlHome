using System.Text.Json;
using System.Text.Json.Nodes;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.LgThinQ.Api;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;

namespace ControlHome.Connectors.LgThinQ.Model;

/// <summary>An entry of <c>GET devices</c>.</summary>
/// <param name="GroupId">Links the separate devices of one appliance (a WashTower's washer and dryer, a WashCombo).</param>
internal sealed record LgDeviceInfo(string DeviceId, string DeviceType, string? ModelName, string? Alias, bool Reportable, string? GroupId)
{
    /// <summary>Parses the device list; entries without an id or a type are skipped.</summary>
    public static IReadOnlyList<LgDeviceInfo> ParseList(JsonElement devices) =>
        [.. devices.Items()
            .Select(d => (Id: d.Str("deviceId"), Info: d.Get("deviceInfo")))
            .Where(d => d.Id is { Length: > 0 and <= DeviceLimits.ExternalIdMaxLength } && d.Info is { ValueKind: JsonValueKind.Object })
            .Select(d => new LgDeviceInfo(
                d.Id!,
                d.Info!.Value.Str("deviceType") ?? "DEVICE_UNKNOWN",
                d.Info.Value.Str("modelName"),
                d.Info.Value.Str("alias"),
                d.Info.Value.Bool("reportable") ?? true,
                d.Info.Value.Str("groupId")))];
}

/// <summary>The ThinQ device types (pythinqconnect <c>DeviceType</c>).</summary>
internal static class LgDeviceTypes
{
    public const string AirConditioner = "DEVICE_AIR_CONDITIONER";
    public const string AirPurifier = "DEVICE_AIR_PURIFIER";
    public const string AirPurifierFan = "DEVICE_AIR_PURIFIER_FAN";
    public const string Dehumidifier = "DEVICE_DEHUMIDIFIER";
    public const string Humidifier = "DEVICE_HUMIDIFIER";
    public const string DishWasher = "DEVICE_DISH_WASHER";
    public const string Dryer = "DEVICE_DRYER";
    public const string Washer = "DEVICE_WASHER";
    public const string WashTower = "DEVICE_WASHTOWER";
    public const string WashTowerWasher = "DEVICE_WASHTOWER_WASHER";
    public const string WashTowerDryer = "DEVICE_WASHTOWER_DRYER";
    public const string WashComboMain = "DEVICE_WASHCOMBO_MAIN";
    public const string WashComboMini = "DEVICE_WASHCOMBO_MINI";
    public const string Styler = "DEVICE_STYLER";
    public const string Refrigerator = "DEVICE_REFRIGERATOR";
    public const string KimchiRefrigerator = "DEVICE_KIMCHI_REFRIGERATOR";
    public const string WineCellar = "DEVICE_WINE_CELLAR";
    public const string RobotCleaner = "DEVICE_ROBOT_CLEANER";

    /// <summary>Appliances with a wash/dry cycle (applianceCycle).</summary>
    public static bool IsLaundry(string type) =>
        type is Washer or Dryer or WashTower or WashTowerWasher or WashTowerDryer or WashComboMain or WashComboMini or Styler;

    public static bool IsRefrigerator(string type) => type is Refrigerator or KimchiRefrigerator or WineCellar;

    /// <summary>The device class (icons, grouping).</summary>
    public static string ClassOf(string type) => type switch
    {
        AirConditioner => DeviceClasses.AirConditioner,
        Washer or WashTower or WashTowerWasher or WashComboMain or WashComboMini => DeviceClasses.Washer,
        Dryer or WashTowerDryer => DeviceClasses.Dryer,
        Refrigerator or KimchiRefrigerator or WineCellar => "refrigerator",
        DishWasher => "dishwasher",
        AirPurifier or AirPurifierFan => "air_purifier",
        Dehumidifier => "dehumidifier",
        Humidifier => "humidifier",
        RobotCleaner or "DEVICE_STICK_CLEANER" => "vacuum",
        _ => Truncate(type.StartsWith("DEVICE_", StringComparison.Ordinal) ? type[7..].ToLowerInvariant() : type.ToLowerInvariant()),
    };

    /// <summary>The order in a group: the device that becomes the others' parent comes first.</summary>
    public static int GroupRank(string type) => type switch
    {
        WashTowerWasher or WashComboMain => 0,
        WashTowerDryer or WashComboMini => 1,
        _ => 2,
    };

    private static string Truncate(string value) => value.Length <= DeviceLimits.DeviceClassMaxLength ? value : value[..DeviceLimits.DeviceClassMaxLength];
}

/// <summary>
/// Maps a ThinQ device (list entry, profile, energy profile) to a <see cref="LgDeviceModel"/>. Curated mappings for air
/// conditioners (switch, thermostat with modes, setpoints and fan), laundry (washer, dryer, WashTower, WashCombo, styler:
/// applianceCycle with the <c>remoteControlEnabled</c> gate), dishwashers, refrigerators (one endpoint per compartment, door
/// contacts), air purifiers, (de)humidifiers and robot cleaners; every property they do not cover becomes a generic
/// attribute (enum / range / boolean / number with its read/write access; writable ones get a setter). Unknown device types
/// and payload drift degrade to generic attributes — the mapper never throws on vendor data.
/// </summary>
internal static class LgDeviceMapper
{
    private static readonly (string Lg, string Mode)[] _acModes =
        [("COOL", "cool"), ("HEAT", "heat"), ("AUTO", "auto"), ("AIR_DRY", "dry"), ("FAN", "fan"), ("AIR_CLEAN", "fan")];

    private static readonly (string Lg, string Fan)[] _fanModes =
        [("AUTO", "auto"), ("SLOW", "quiet"), ("LOW", "low"), ("MID", "medium"), ("HIGH", "high"), ("POWER", "turbo")];

    private static readonly string[] _modeOrder = ["off", "cool", "heat", "auto", "dry", "fan"];

    private static readonly string[] _temperatureResources = ["temperatureInUnits", "temperature"];

    public static LgDeviceModel Map(LgDeviceInfo info, JsonElement profile, JsonElement? energyProfile, string? parentExternalId = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        var parsed = LgProfile.Parse(profile);
        var builder = new LgModelBuilder(parsed);

        // Endpoints in profile order, main first (left out when nothing ends up on it).
        builder.Endpoint(EndpointKeys.Main);

        foreach (var property in parsed.Properties)
        {
            var key = property.Place.EndpointKey;
            if (key != EndpointKeys.Main)
            {
                var source = property.Place.Sub ?? (property.Place.Selector == LgPlace.LocationNameSelector ? property.Place.SelectorValue : property.Place.Location);
                builder.Endpoint(key, source is null ? null : EndpointKeys.NameOf(source));
            }
        }

        var type = info.DeviceType;
        foreach (var endpoint in builder.Endpoints.ToList())
        {
            var key = endpoint.Key;
            if (type == LgDeviceTypes.AirConditioner)
            {
                MapAirConditioner(builder, key);
            }

            if (LgDeviceTypes.IsLaundry(type) || type is LgDeviceTypes.DishWasher or LgDeviceTypes.RobotCleaner)
            {
                MapCycle(builder, key, robot: type == LgDeviceTypes.RobotCleaner);
            }

            MapDoor(builder, key);
            MapHumidity(builder, key);
            MapAirQuality(builder, key);
            MapBattery(builder, key);
            MapPowerSwitch(builder, key);
            MapGeneric(builder, key);
        }

        var notifications = MapNotifications(builder, parsed);
        var energy = EnergyProperties(builder, energyProfile);
        var device = new DiscoveredDevice(
            info.DeviceId,
            Name(info),
            LgDeviceTypes.ClassOf(type),
            "LG",
            info.ModelName,
            null,
            parentExternalId,
            new Dictionary<string, string>(StringComparer.Ordinal),
            null,
            []);
        return builder.Build(info.DeviceId, type, device, energy, notifications);
    }

    /// <summary>
    /// Parent/child for appliances made of several devices (a WashTower's separate washer and dryer, a WashCombo's main and
    /// mini): devices sharing a <c>groupId</c> become children of the group's first device (the washer, the main unit).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parents(IReadOnlyList<LgDeviceInfo> devices)
    {
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in devices.Where(d => d.GroupId is { Length: > 0 }).GroupBy(d => d.GroupId!, StringComparer.Ordinal))
        {
            var members = group.OrderBy(d => LgDeviceTypes.GroupRank(d.DeviceType)).ThenBy(d => d.DeviceId, StringComparer.Ordinal).ToList();
            foreach (var member in members.Skip(1))
            {
                parents[member.DeviceId] = members[0].DeviceId;
            }
        }

        return parents;
    }

    private static string Name(LgDeviceInfo info)
    {
        var name = info.Alias is { Length: > 0 } alias ? alias.Trim() : info.ModelName ?? info.DeviceType;
        return name.Length <= DeviceLimits.NameMaxLength ? name : name[..DeviceLimits.NameMaxLength];
    }

    // ---- air conditioner ---------------------------------------------------------------------------------------------

    private static void MapAirConditioner(LgModelBuilder builder, string endpoint)
    {
        var ep = builder.Endpoint(endpoint);
        var power = builder.Find(endpoint, "operation", "airConOperationMode");
        var hasPower = power is { Type: LgPropertyType.Enum } && power.Values.Contains("POWER_ON") && power.Values.Contains("POWER_OFF");
        if (hasPower)
        {
            ep.Add(CapabilityIds.Switch);
            builder.Claim(power);
            builder.Read(endpoint, CapabilityIds.Switch, "on", status => PowerOf(power!, status));
            AddPowerCommands(builder, endpoint, power!);
        }

        var jobMode = builder.Find(endpoint, "airConJobMode", "currentJobMode");
        var modes = new List<(string Lg, string Mode)>();
        foreach (var (lg, mode) in _acModes)
        {
            if (jobMode is not null && jobMode.Values.Contains(lg) && modes.All(m => m.Mode != mode))
            {
                modes.Add((lg, mode));
            }
        }

        var temperatures = _temperatureResources
            .SelectMany(resource => new[] { "targetTemperature", "coolTargetTemperature", "heatTargetTemperature", "autoTargetTemperature", "currentTemperature" }
                .Select(key => (Key: key, Property: builder.Find(endpoint, resource, key))))
            .Where(t => t.Property is not null)
            .ToList();
        if (modes.Count == 0 && temperatures.Count == 0)
        {
            return;
        }

        ep.Add(CapabilityIds.Thermostat);
        if (jobMode is not null && jobMode.Values.All(v => modes.Any(m => m.Lg == v)))
        {
            builder.Claim(jobMode);
        }

        // Every temperature property of the unit lists (°F twins included) belongs to the thermostat.
        builder.Claim([.. builder.In(endpoint).Where(p => _temperatureResources.Contains(p.Resource)
            && (p.Key.EndsWith("Temperature", StringComparison.Ordinal) || p.Key == "unit"))]);

        // mode: "off" while powered off, else the job mode.
        var modeValues = _modeOrder.Where(m => (m == "off" && hasPower && power!.Writable) || modes.Any(x => x.Mode == m)).ToList();
        if (modeValues.Count > 0)
        {
            ep.Constrain(CapabilityIds.Thermostat, "mode", new AttributeConstraint(Values: modeValues));
        }

        builder.Read(endpoint, CapabilityIds.Thermostat, "mode", status =>
        {
            if (hasPower && PowerOf(power!, status) == StateValue.False)
            {
                return StateValue.String("off");
            }

            var current = LgValues.Text(jobMode?.Read(status));
            return modes.FirstOrDefault(m => m.Lg == current) is { Mode: { } mapped } ? StateValue.String(mapped) : null;
        });
        builder.Command(endpoint, CapabilityIds.Thermostat, "setMode", (arguments, status) =>
        {
            var mode = arguments.GetProperty("mode").GetString();
            if (mode == "off")
            {
                return hasPower && power!.WriteValues.Contains("POWER_OFF")
                    ? LgCommandResult.Of(power.Call("POWER_OFF"))
                    : LgCommandResult.Unsupported("The air conditioner cannot be switched off remotely.");
            }

            if (jobMode is not { Writable: true } || modes.FirstOrDefault(m => m.Mode == mode) is not { Lg: { } lg } || !jobMode.WriteValues.Contains(lg))
            {
                return LgCommandResult.Unsupported("The air conditioner does not offer this mode remotely.");
            }

            // Powered off: switch on first, then the mode (two calls, like the official app).
            return hasPower && PowerOf(power!, status) == StateValue.False && power!.WriteValues.Contains("POWER_ON")
                ? LgCommandResult.Of(power.Call("POWER_ON"), jobMode.Call(lg))
                : LgCommandResult.Of(jobMode.Call(lg));
        });

        MapSetpoints(builder, endpoint, modes, jobMode);
        MapFan(builder, endpoint);
    }

    private static void MapSetpoints(LgModelBuilder builder, string endpoint, List<(string Lg, string Mode)> modes, LgProperty? jobMode)
    {
        var ep = builder.Endpoint(endpoint);
        LgProperty? Temperature(string key) => _temperatureResources.Select(r => builder.Find(endpoint, r, key)).FirstOrDefault(p => p is not null);

        var target = Temperature("targetTemperature");
        var current = Temperature("currentTemperature");
        var byMode = new Dictionary<string, LgProperty>(StringComparer.Ordinal);
        foreach (var (mode, key) in new[] { ("cool", "coolTargetTemperature"), ("heat", "heatTargetTemperature"), ("auto", "autoTargetTemperature") })
        {
            if (Temperature(key) is { } property)
            {
                byMode[mode] = property;
            }
        }

        if (current is not null)
        {
            builder.Read(endpoint, CapabilityIds.Thermostat, "currentTemperature", status => Celsius(current, status));
        }

        string? ModeOf(JsonElement status) => modes.FirstOrDefault(m => m.Lg == LgValues.Text(jobMode?.Read(status))).Mode;

        builder.Read(endpoint, CapabilityIds.Thermostat, "targetTemperature", status =>
        {
            var mode = ModeOf(status);
            var forMode = mode is not null && byMode.TryGetValue(mode, out var specific) ? Celsius(specific, status) : null;
            return forMode ?? (target is null ? null : Celsius(target, status));
        });

        // Writable setpoints: the mode's own (cool/heat/auto), else the generic target temperature.
        var writable = byMode.Values.Append(target).OfType<LgProperty>().Where(p => p.Writable && p.Unit != "F").ToList();
        if (writable.Count == 0)
        {
            return;
        }

        var ranges = writable.Select(p => p.WriteRange ?? p.ReadRange).OfType<LgRange>().ToList();
        if (ranges.Count > 0)
        {
            var cases = byMode
                .Where(p => p.Value.Writable && (p.Value.WriteRange ?? p.Value.ReadRange) is not null)
                .ToDictionary(p => p.Key, p => Narrow(p.Value.WriteRange ?? p.Value.ReadRange!), StringComparer.Ordinal);
            var all = Narrow(new LgRange(ranges.Min(r => r.Min), ranges.Max(r => r.Max), ranges.Min(r => r.Step), []));
            ep.Constrain(
                CapabilityIds.Thermostat,
                "targetTemperature",
                cases.Count > 0 ? all with { DependsOn = "mode", Cases = cases } : all);
        }

        builder.Command(endpoint, CapabilityIds.Thermostat, "setTargetTemperature", (arguments, status) =>
        {
            var value = arguments.GetProperty("targetTemperature").GetDouble();
            var mode = ModeOf(status);
            var property = (mode is not null && byMode.TryGetValue(mode, out var specific) && specific.Writable ? specific : null)
                ?? (target is { Writable: true } ? target : writable[0]);
            var range = property.WriteRange ?? property.ReadRange;
            if (range is not null && !range.Allows(value))
            {
                return LgCommandResult.Reject(CommandRejectionCodes.InvalidArguments, "The temperature is outside the air conditioner's range for this mode.");
            }

            // Like the SDK: the value with its unit, in the unit list's resource.
            var call = property.Place.Selector == LgPlace.UnitSelector
                ? property.Call(LgValues.Node(value))
                : new LgCall(Merge(property.Place.Payload(property.Key, LgValues.Node(value)), property.Place.Payload("unit", "C")), property.Place.Target);
            return LgCommandResult.Of(call);
        });
    }

    private static void MapFan(LgModelBuilder builder, string endpoint)
    {
        var fan = builder.Find(endpoint, "airFlow", "windStrengthDetail") ?? builder.Find(endpoint, "airFlow", "windStrength");
        if (fan is not { Type: LgPropertyType.Enum })
        {
            return;
        }

        var map = _fanModes.Where(f => fan.Values.Contains(f.Lg)).ToList();
        if (map.Count == 0)
        {
            return;
        }

        var ep = builder.Endpoint(endpoint);
        ep.Constrain(CapabilityIds.Thermostat, "fanMode", new AttributeConstraint(Values: [.. map.Select(f => f.Fan)]));
        if (fan.Values.All(v => map.Any(f => f.Lg == v)))
        {
            builder.Claim(fan);
        }

        builder.Read(endpoint, CapabilityIds.Thermostat, "fanMode", status =>
            map.FirstOrDefault(f => f.Lg == LgValues.Text(fan.Read(status))) is { Fan: { } mapped } ? StateValue.String(mapped) : null);
        builder.Command(endpoint, CapabilityIds.Thermostat, "setFanMode", (arguments, _) =>
        {
            var wanted = arguments.GetProperty("fanMode").GetString();
            return map.FirstOrDefault(f => f.Fan == wanted) is { Lg: { } lg } && fan.WriteValues.Contains(lg)
                ? LgCommandResult.Of(fan.Call(lg))
                : LgCommandResult.Unsupported("The air conditioner does not offer this fan speed remotely.");
        });
    }

    // ---- cycles (laundry, dishwasher, robot cleaner) -----------------------------------------------------------------

    private static void MapCycle(LgModelBuilder builder, string endpoint, bool robot)
    {
        var state = builder.Find(endpoint, "runState", "currentState");
        if (state is null)
        {
            return;
        }

        var ep = builder.Endpoint(endpoint);
        ep.Add(CapabilityIds.ApplianceCycle);
        builder.Claim(state);
        builder.Read(endpoint, CapabilityIds.ApplianceCycle, "machineState", status =>
            LgValues.Text(state.Read(status)) is { } current ? StateValue.String(MachineState(current, robot)) : null);
        builder.Read(endpoint, CapabilityIds.ApplianceCycle, "phase", status =>
            LgValues.Text(state.Read(status)) is { } current ? StateValue.String(Phase(current)) : null);

        var remainHour = builder.Find(endpoint, "timer", "remainHour");
        var remainMinute = builder.Find(endpoint, "timer", "remainMinute");
        if (remainHour is not null || remainMinute is not null)
        {
            builder.Claim(remainHour, remainMinute);
            builder.Read(endpoint, CapabilityIds.ApplianceCycle, "remainingMinutes", status =>
            {
                var hours = remainHour is null ? null : LgValues.Number(remainHour.Read(status));
                var minutes = remainMinute is null ? null : LgValues.Number(remainMinute.Read(status));
                return hours is null && minutes is null ? null : StateValue.Number(Math.Max(0, Math.Round(((hours ?? 0) * 60) + (minutes ?? 0))));
            });
        }

        // The appliance's own "remote start" switch gates start/pause/stop; appliances without one (robot cleaners) are
        // always remote-controllable.
        var remote = builder.Find(endpoint, "remoteControlEnable", "remoteControlEnabled");
        builder.Claim(remote);
        builder.Read(endpoint, CapabilityIds.ApplianceCycle, "remoteControlEnabled", status =>
            remote is null ? StateValue.True : LgValues.Flag(remote.Read(status)) is { } enabled ? StateValue.Bool(enabled) : null);

        var operation = builder.Find(endpoint, p => p.Resource == "operation" && p.Writable && p.Type == LgPropertyType.Enum
            && p.Key.EndsWith("OperationMode", StringComparison.Ordinal));
        if (operation is null)
        {
            return;
        }

        var values = operation.WriteValues;
        var start = values.Contains("START") ? "START" : null;
        var pause = values.Contains("PAUSE") ? "PAUSE" : values.Contains("STOP") ? "STOP" : null;
        var stop = robot
            ? values.Contains("HOMING") ? "HOMING" : values.Contains("STOP") && pause != "STOP" ? "STOP" : null
            : values.Contains("POWER_OFF") ? "POWER_OFF" : values.Contains("STOP") && pause != "STOP" ? "STOP" : null;
        foreach (var (command, value) in new[] { ("start", start), ("pause", pause), ("stop", stop) })
        {
            builder.Command(endpoint, CapabilityIds.ApplianceCycle, command, (_, _) => value is null
                ? LgCommandResult.Unsupported("The appliance does not offer this remotely.")
                : LgCommandResult.Of(operation.Call(value)));
        }

        // Covered entirely only when no other operation value (e.g. POWER_ON, WAKE_UP) is left for the generic setter.
        if (values.All(v => v == start || v == pause || v == stop))
        {
            builder.Claim(operation);
        }
    }

    /// <summary>ThinQ run states → <c>applianceCycle.machineState</c> (unknown states while running count as running).</summary>
    internal static string MachineState(string state, bool robot) => state switch
    {
        "END" or "COMPLETE" or "CLEANING_IS_DONE" => "finished",
        "ERROR" => "error",
        "PAUSE" or "RINSE_HOLD" or "FROZEN_PREVENT_PAUSE" or "PAUSED" => "paused",
        "POWER_OFF" or "INITIAL" or "SLEEP" or "STANDBY" or "RESERVED" or "FROZEN_PREVENT_INITIAL" or "CHARGING" or "IDLE" or "DONE" or "STOP"
            or "DISPLAY_LOADITEM" or "NONE" => robot && state == "DONE" ? "finished" : "idle",
        _ => "running",
    };

    private static string Phase(string state)
    {
        var phase = state.ToLowerInvariant();
        return phase.Length <= 64 ? phase : phase[..64];
    }

    // ---- sensors and switches -----------------------------------------------------------------------------------------

    private static void MapDoor(LgModelBuilder builder, string endpoint)
    {
        var door = builder.Find(endpoint, "doorStatus", "doorState");
        if (door is not { Type: LgPropertyType.Enum } || builder.Endpoint(endpoint).Has(CapabilityIds.Contact))
        {
            return;
        }

        builder.Endpoint(endpoint).Add(CapabilityIds.Contact);
        builder.Claim(door);
        builder.Read(endpoint, CapabilityIds.Contact, "open", status => LgValues.Text(door.Read(status)) switch
        {
            "OPEN" => StateValue.True,
            "CLOSE" or "CLOSED" => StateValue.False,
            _ => null,
        });
    }

    private static void MapHumidity(LgModelBuilder builder, string endpoint)
    {
        var humidity = builder.Find(endpoint, "humidity", "currentHumidity") ?? builder.Find(endpoint, "airQualitySensor", "humidity");
        if (humidity is not { Readable: true, Type: LgPropertyType.Number or LgPropertyType.Range } || builder.Endpoint(endpoint).Has(CapabilityIds.Humidity))
        {
            return;
        }

        builder.Endpoint(endpoint).Add(CapabilityIds.Humidity);
        builder.Claim(humidity);
        builder.Read(endpoint, CapabilityIds.Humidity, "humidity", status =>
            LgValues.Number(humidity.Read(status)) is { } value && value is >= 0 and <= 100 ? StateValue.Number(value) : null);
    }

    private static void MapAirQuality(LgModelBuilder builder, string endpoint)
    {
        var pm25 = builder.Find(endpoint, "airQualitySensor", "PM2");
        if (pm25 is not { Readable: true, Type: LgPropertyType.Number or LgPropertyType.Range })
        {
            return;
        }

        builder.Endpoint(endpoint).Add(CapabilityIds.AirQuality);
        builder.Claim(pm25);
        builder.Read(endpoint, CapabilityIds.AirQuality, "pm25", status =>
            LgValues.Number(pm25.Read(status)) is { } value && value >= 0 ? StateValue.Number(value) : null);
    }

    private static void MapBattery(LgModelBuilder builder, string endpoint)
    {
        var battery = builder.Find(endpoint, "battery", "percent");
        if (battery is not { Readable: true, Type: LgPropertyType.Number or LgPropertyType.Range })
        {
            return;
        }

        builder.Endpoint(endpoint).Add(CapabilityIds.Battery);
        builder.Claim(battery);
        builder.Read(endpoint, CapabilityIds.Battery, "battery", status =>
            LgValues.Number(battery.Read(status)) is { } value && value is >= 0 and <= 100 ? StateValue.Number(Math.Round(value)) : null);
    }

    /// <summary>Any <c>operation.*OperationMode</c> with POWER_ON / POWER_OFF becomes a switch (purifiers, dehumidifiers, hoods, ...).</summary>
    private static void MapPowerSwitch(LgModelBuilder builder, string endpoint)
    {
        var ep = builder.Endpoint(endpoint);
        if (ep.Has(CapabilityIds.Switch))
        {
            return;
        }

        // Only a readable one: a write-only power mode (a washer's) would be a switch without state.
        var power = builder.Find(endpoint, p => !builder.IsClaimed(p) && p.Readable && p.Resource == "operation" && p.Type == LgPropertyType.Enum
            && p.Key.EndsWith("OperationMode", StringComparison.Ordinal) && p.Values.Contains("POWER_ON") && p.Values.Contains("POWER_OFF"));
        if (power is null)
        {
            return;
        }

        ep.Add(CapabilityIds.Switch);
        if (power.Values.All(v => v is "POWER_ON" or "POWER_OFF"))
        {
            builder.Claim(power);
        }

        builder.Read(endpoint, CapabilityIds.Switch, "on", status => PowerOf(power, status));
        AddPowerCommands(builder, endpoint, power);
    }

    private static void AddPowerCommands(LgModelBuilder builder, string endpoint, LgProperty power)
    {
        LgCommandResult Set(string value) => power.Writable && power.WriteValues.Contains(value)
            ? LgCommandResult.Of(power.Call(value))
            : LgCommandResult.Unsupported("The appliance cannot be switched remotely.");

        builder.Command(endpoint, CapabilityIds.Switch, "turnOn", (_, _) => Set("POWER_ON"));
        builder.Command(endpoint, CapabilityIds.Switch, "turnOff", (_, _) => Set("POWER_OFF"));
        builder.Command(endpoint, CapabilityIds.Switch, "toggle", (_, status) => Set(PowerOf(power, status) == StateValue.True ? "POWER_OFF" : "POWER_ON"));
    }

    private static StateValue? PowerOf(LgProperty power, JsonElement status) => LgValues.Text(power.Read(status)) switch
    {
        "POWER_ON" => StateValue.True,
        "POWER_OFF" => StateValue.False,
        _ => null,
    };

    // ---- generic ----------------------------------------------------------------------------------------------------

    private static void MapGeneric(LgModelBuilder builder, string endpoint)
    {
        var ep = builder.Endpoint(endpoint);
        foreach (var property in builder.In(endpoint).ToList())
        {
            // Temperature units are a selector, not a value; °F twins of °C values are left out.
            if (builder.IsClaimed(property) || property.Key == "unit" || property.Unit == "F" || property.Place.SelectorValue == "F")
            {
                continue;
            }

            var celsius = property.Unit == "C";
            var key = celsius && property.Key.Length > 1 && property.Key.EndsWith("TemperatureC", StringComparison.Ordinal) ? property.Key[..^1] : property.Key;
            ValueSchema? schema = property.Type switch
            {
                LgPropertyType.Enum when property.Values.Count > 0 => new EnumSchema(property.Values),
                LgPropertyType.Enum => new StringSchema(255),
                LgPropertyType.Boolean => BoolSchema.Instance,
                LgPropertyType.Range when property.Range is { } range => new NumberSchema(range.Min, range.Max, range.Step, celsius ? Units.Celsius : null, range.IsInteger),
                LgPropertyType.Range or LgPropertyType.Number => new NumberSchema(null, null, null, celsius ? Units.Celsius : null),
                LgPropertyType.String => new StringSchema(255),
                _ => JsonValueSchema.Instance,
            };
            var writable = property.Writable && property.Type != LgPropertyType.Other && !(property.Type == LgPropertyType.String && property.Constant is not null);
            var (attribute, setter) = ep.AddGeneric(key, property.Resource + char.ToUpperInvariant(key[0]) + key[1..], schema, writable);
            builder.Claim(property);
            if (property.Readable)
            {
                // A value outside the profile's own range or values (LG reports 0 for an unset 1–19 h delay) is left out.
                var type = property.Type;
                builder.Read(endpoint, CapabilityIds.Generic, attribute, status =>
                    (LgValues.Generic(type, property.Read(status)) ?? (property.Constant is { } constant ? StateValue.String(constant) : null)) is { } value
                    && ValueSchemaValidator.Accepts(schema, value, null)
                        ? value
                        : null);
            }

            if (setter is not null)
            {
                builder.Command(endpoint, CapabilityIds.Generic, setter, (arguments, _) =>
                {
                    var value = arguments.GetProperty(attribute);
                    return property.AllowsWrite(value)
                        ? LgCommandResult.Of(property.Call(LgValues.Argument(property.Type, value)))
                        : LgCommandResult.Reject(CommandRejectionCodes.InvalidArguments, "The appliance does not accept this value remotely.");
                });
            }
        }
    }

    private static Dictionary<string, string> MapNotifications(LgModelBuilder builder, LgProfile profile)
    {
        var endpoints = new Dictionary<string, string>(StringComparer.Ordinal);
        var first = FirstEndpoint(builder);
        foreach (var group in profile.Notifications.GroupBy(n => n.Sub is null ? first : EndpointKeys.Of(n.Sub)))
        {
            var ep = builder.Endpoint(group.Key);
            var codes = group.Select(n => n.Code).Distinct(StringComparer.Ordinal).ToList();
            ep.AddGeneric(
                LgDeviceModel.NotificationAttribute,
                "pushNotification",
                new MomentarySchema { Events = [.. codes.Select(c => c.ToLowerInvariant())] },
                writable: false);
            foreach (var code in codes)
            {
                endpoints.TryAdd(code, group.Key);
            }
        }

        return endpoints;
    }

    private static List<(string Endpoint, string Property)> EnergyProperties(LgModelBuilder builder, JsonElement? energyProfile)
    {
        var result = new List<(string, string)>();
        if (energyProfile is not { } profile)
        {
            return result;
        }

        var properties = (profile.Get("result") ?? profile).Array("property")
            .Where(p => p.ValueKind == JsonValueKind.String)
            .Select(p => p.GetString()!)
            .Where(p => p.Length is > 0 and <= 64)
            .Distinct(StringComparer.Ordinal);
        var keys = builder.Endpoints.Where(e => !e.IsEmpty).Select(e => e.Key).ToList();
        foreach (var property in properties)
        {
            // A sub-device's energy (e.g. "energyUsageDryer") goes to its endpoint; the rest to the first endpoint.
            var endpoint = keys.FirstOrDefault(k => k != EndpointKeys.Main && property.Contains(k, StringComparison.OrdinalIgnoreCase))
                ?? FirstEndpoint(builder);
            result.Add((endpoint, property));
        }

        return result;
    }

    /// <summary>The device-level endpoint: <c>main</c> when anything is on it, else the first endpoint that has something.</summary>
    private static string FirstEndpoint(LgModelBuilder builder) =>
        builder.Endpoints.FirstOrDefault(e => !e.IsEmpty)?.Key ?? EndpointKeys.Main;

    // ---- helpers ----------------------------------------------------------------------------------------------------

    /// <summary>A temperature in °C from a property: a °C value, or a °F one converted (when the device reports in °F).</summary>
    private static StateValue? Celsius(LgProperty property, JsonElement status)
    {
        if (LgValues.Number(property.Read(status)) is not { } value)
        {
            return null;
        }

        var unit = property.Unit ?? LgValues.Text(property.Place.ResourceIn(status)?.Get("unit"));
        return StateValue.Number(unit == "F" ? Math.Round((value - 32) * 5 / 9 * 2) / 2 : value);
    }

    /// <summary>A setpoint range narrowed to the thermostat catalogue (5–35 °C).</summary>
    private static AttributeConstraint Narrow(LgRange range)
    {
        var min = Math.Max(5, range.Min);
        var max = Math.Min(35, range.Max);
        return new AttributeConstraint(Min: min, Max: Math.Max(min, max), Step: range.Step % 0.5 == 0 ? range.Step : null);
    }

    private static JsonObject Merge(JsonObject a, JsonObject b) => (JsonObject)LgStatus.Merge(a, b)!;
}

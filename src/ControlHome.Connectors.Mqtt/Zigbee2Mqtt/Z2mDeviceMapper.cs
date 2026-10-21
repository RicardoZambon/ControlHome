using System.Text.Json;
using System.Text.Json.Nodes;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.Mqtt.Model;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;

namespace ControlHome.Connectors.Mqtt.Zigbee2Mqtt;

/// <summary>
/// Maps one entry of Zigbee2MQTT's retained <c>bridge/devices</c> to a device: <c>definition.exposes</c> and their access
/// bits (1 published, 2 settable, 4 gettable) become capabilities. Specific exposes: <c>light</c> → switch/level/color,
/// <c>switch</c> → switch, <c>cover</c> → cover, <c>lock</c> → lock, <c>climate</c> → thermostat. Generic exposes by name:
/// temperature, humidity, occupancy, presence, contact, water_leak, smoke, battery, power, energy, co2, pm25, voc → their
/// capabilities; <c>action</c> → button events; illuminance, voltage, current and everything else (composite, list,
/// unknown) → the endpoint's <c>generic</c> capability. Each expose's <c>endpoint</c> (e.g. <c>l1</c>) is an endpoint key.
/// </summary>
internal static class Z2mDeviceMapper
{
    public const string BridgeDeviceClass = "bridge";

    private const int Published = 1;
    private const int Settable = 2;
    private const int GettableBit = 4;

    private static readonly string[] _modeOrder = ["off", "cool", "heat", "auto", "dry", "fan"];
    private static readonly string[] _fanOrder = ["auto", "quiet", "low", "medium", "high", "turbo"];
    private static readonly string[] _setpoints = ["occupied_heating_setpoint", "current_heating_setpoint", "occupied_cooling_setpoint"];

    /// <summary>
    /// The device for one <c>bridge/devices</c> entry, or <c>null</c> for the coordinator, disabled devices and devices
    /// without a definition (not interviewed yet).
    /// </summary>
    /// <param name="parentExternalId">The bridge device's external id (the coordinator's IEEE address), if known.</param>
    public static Z2mDevice? Map(JsonElement entry, string baseTopic, string? parentExternalId)
    {
        var ieee = entry.Str("ieee_address");
        var friendly = entry.Str("friendly_name");
        if (string.IsNullOrWhiteSpace(ieee) || string.IsNullOrWhiteSpace(friendly)
            || string.Equals(entry.Str("type"), "Coordinator", StringComparison.OrdinalIgnoreCase)
            || entry.Bool("disabled") == true
            || entry.Get("definition") is not { ValueKind: JsonValueKind.Object } definition)
        {
            return null;
        }

        var context = new MapContext(baseTopic, friendly);
        foreach (var expose in definition.Array("exposes"))
        {
            context.MapExpose(expose, expose.Str("endpoint"));
        }

        var description = definition.Str("description") ?? string.Empty;
        var discovered = new DiscoveredDevice(
            ieee,
            friendly,
            context.DeviceClass(description),
            definition.Str("vendor") ?? entry.Str("manufacturer"),
            definition.Str("model") ?? entry.Str("model_id"),
            entry.Str("software_build_id"),
            parentExternalId,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["zigbee"] = ieee },
            SuggestedRoom(friendly),
            context.Builder.Build());
        return new Z2mDevice(
            discovered,
            baseTopic,
            friendly,
            isBridge: false,
            context.States,
            context.Actions,
            context.Commands,
            string.Equals(entry.Str("power_source"), "Battery", StringComparison.OrdinalIgnoreCase) ? [] : context.Gettable);
    }

    /// <summary>
    /// The bridge (coordinator) device: parent of the other devices, with a generic <c>permitJoin</c> state, the
    /// Zigbee2MQTT version and a <c>permitJoin(seconds)</c> command (<c>bridge/request/permit_join</c>).
    /// </summary>
    /// <param name="info">The retained <c>bridge/info</c> payload, if received.</param>
    public static Z2mDevice MapBridge(string coordinatorIeee, JsonElement? info, string baseTopic)
    {
        var builder = new DeviceBuilder();
        var main = builder.Endpoint(DeviceBuilder.Main);
        var permitJoin = main.AddGeneric("permitJoin", BoolSchema.Instance);
        var version = main.AddGeneric("version", new StringSchema(64), category: AttributeCategory.Diagnostic);
        var command = main.AddGenericCommand(
            "permitJoin",
            [new ArgumentDefinition("seconds", new NumberSchema(0, 254, 1, "s", Integer: true), Required: true)],
            ConfirmationMode.AckIsFinal);

        var states = new List<Z2mStateBinding>
        {
            new("permit_join", DeviceBuilder.Main, CapabilityIds.Generic, permitJoin, v => v.ValueKind is JsonValueKind.True or JsonValueKind.False ? StateValue.Bool(v.GetBoolean()) : null),
            new("version", DeviceBuilder.Main, CapabilityIds.Generic, version, v => v.ValueKind == JsonValueKind.String ? StateValue.String(v.GetString()!) : null),
        };
        var commands = new Dictionary<(string, string, string), Z2mCommandBuilder>
        {
            [(DeviceBuilder.Main, CapabilityIds.Generic, command)] = c =>
                ($"{baseTopic}/bridge/request/permit_join", new JsonObject { ["time"] = (int)c.Number("seconds") }),
        };

        var coordinator = info?.Get("coordinator");
        var discovered = new DiscoveredDevice(
            coordinatorIeee,
            "Zigbee2MQTT bridge",
            BridgeDeviceClass,
            "Zigbee2MQTT",
            coordinator?.Str("type") ?? "Zigbee coordinator",
            info?.Str("version"),
            null,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["zigbee"] = coordinatorIeee },
            null,
            builder.Build());
        return new Z2mDevice(discovered, baseTopic, "bridge", isBridge: true, states, [], commands, []);
    }

    /// <summary>"kitchen/ceiling" → "kitchen" (Zigbee2MQTT users often group friendly names by room).</summary>
    private static string? SuggestedRoom(string friendlyName)
    {
        var slash = friendlyName.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? friendlyName[..slash].Replace('_', ' ').Trim() : null;
    }

    private static int Access(JsonElement expose) => expose.Int("access") ?? Published;

    private static bool Has(JsonElement expose, int bit) => (Access(expose) & bit) == bit;

    /// <summary>The mapping state of one device.</summary>
    private sealed class MapContext(string baseTopic, string friendlyName)
    {
        private readonly HashSet<string> _kinds = new(StringComparer.Ordinal);

        public DeviceBuilder Builder { get; } = new();

        public List<Z2mStateBinding> States { get; } = [];

        public List<Z2mActionBinding> Actions { get; } = [];

        public Dictionary<(string Endpoint, string Capability, string Command), Z2mCommandBuilder> Commands { get; } = [];

        public List<string> Gettable { get; } = [];

        private string SetTopic => $"{baseTopic}/{friendlyName}/set";

        public string? DeviceClass(string description)
        {
            var text = description.ToLowerInvariant();
            if (_kinds.Contains("lock"))
            {
                return DeviceClasses.Lock;
            }

            if (_kinds.Contains("cover"))
            {
                return text.Contains("garage", StringComparison.Ordinal) ? DeviceClasses.Garage
                    : text.Contains("gate", StringComparison.Ordinal) ? DeviceClasses.Gate
                    : text.Contains("curtain", StringComparison.Ordinal) ? DeviceClasses.Curtain
                    : DeviceClasses.Blind;
            }

            if (_kinds.Contains("climate"))
            {
                return DeviceClasses.Thermostat;
            }

            if (_kinds.Contains("light"))
            {
                return DeviceClasses.Light;
            }

            if (_kinds.Contains("switch"))
            {
                return text.Contains("plug", StringComparison.Ordinal) || text.Contains("socket", StringComparison.Ordinal)
                    || text.Contains("outlet", StringComparison.Ordinal) ? DeviceClasses.Outlet : DeviceClasses.Switch;
            }

            if (_kinds.Contains("action"))
            {
                return DeviceClasses.Button;
            }

            return _kinds.Count > 0 ? DeviceClasses.Sensor : null;
        }

        public void MapExpose(JsonElement expose, string? endpoint)
        {
            var key = endpoint is { Length: > 0 } ? Naming.EndpointKey(endpoint) : DeviceBuilder.Main;
            switch (expose.Str("type"))
            {
                case "light":
                    MapLight(expose, key);
                    break;
                case "switch":
                    MapSwitch(expose, key);
                    break;
                case "cover":
                    MapCover(expose, key);
                    break;
                case "lock":
                    MapLock(expose, key);
                    break;
                case "climate":
                    MapClimate(expose, key);
                    break;
                case "fan":
                    foreach (var feature in expose.Array("features"))
                    {
                        MapGeneric(Builder.Endpoint(key), feature);
                    }

                    break;
                default:
                    MapStandalone(expose, key);
                    break;
            }
        }

        private void MapLight(JsonElement light, string key)
        {
            var features = light.Array("features").ToList();
            var endpoint = Builder.Place(CapabilityIds.Switch, key, key + "_light", null);
            _kinds.Add("light");
            JsonElement? xy = null;
            JsonElement? hs = null;
            foreach (var feature in features)
            {
                switch (feature.Str("name"))
                {
                    case "state" when feature.Str("type") == "binary":
                        MapOnOff(endpoint, feature);
                        break;
                    case "brightness" when feature.Str("type") == "numeric":
                        MapBrightness(endpoint, feature);
                        break;
                    case "color_temp" when feature.Str("type") == "numeric":
                        MapColorTemperature(endpoint, feature);
                        break;
                    case "color_xy" when feature.Str("type") == "composite":
                        xy = feature;
                        break;
                    case "color_hs" when feature.Str("type") == "composite":
                        hs = feature;
                        break;
                    default:
                        MapGeneric(endpoint, feature);
                        break;
                }
            }

            if (xy is not null || hs is not null)
            {
                MapColor(endpoint, (hs ?? xy)!.Value, supportsHs: hs is not null);
            }
        }

        private void MapSwitch(JsonElement expose, string key)
        {
            var endpoint = Builder.Place(CapabilityIds.Switch, key, key + "_switch", null);
            _kinds.Add("switch");
            foreach (var feature in expose.Array("features"))
            {
                if (feature.Str("name") == "state" && feature.Str("type") == "binary")
                {
                    MapOnOff(endpoint, feature);
                }
                else
                {
                    MapGeneric(endpoint, feature);
                }
            }
        }

        private void MapOnOff(EndpointBuilder endpoint, JsonElement feature)
        {
            var property = feature.Str("property") ?? "state";
            var on = feature.Get("value_on") ?? JsonSerializer.SerializeToElement("ON");
            var off = feature.Get("value_off") ?? JsonSerializer.SerializeToElement("OFF");
            var toggle = feature.Get("value_toggle");
            endpoint.Add(CapabilityIds.Switch);
            States.Add(new Z2mStateBinding(property, endpoint.Key, CapabilityIds.Switch, "on", v => Binary(v, on, off)));
            if (Has(feature, Settable))
            {
                Command(endpoint.Key, CapabilityIds.Switch, "turnOn", _ => Payload(property, on));
                Command(endpoint.Key, CapabilityIds.Switch, "turnOff", _ => Payload(property, off));
                Command(endpoint.Key, CapabilityIds.Switch, "toggle", _ => Payload(property, toggle ?? JsonSerializer.SerializeToElement("TOGGLE")));
            }

            Get(feature, property);
        }

        private void MapBrightness(EndpointBuilder endpoint, JsonElement feature)
        {
            var property = feature.Str("property") ?? "brightness";
            var max = feature.Num("value_max") is { } m && m > 0 ? m : 254;
            endpoint.Add(CapabilityIds.Level);
            States.Add(new Z2mStateBinding(
                property,
                endpoint.Key,
                CapabilityIds.Level,
                "level",
                v => Conversions.Number(v) is { } b ? StateValue.Number(Conversions.Clamp(b * 100 / max, 0, 100)) : null));
            if (Has(feature, Settable))
            {
                Command(endpoint.Key, CapabilityIds.Level, "setLevel", c =>
                {
                    var payload = new JsonObject { [property] = Json.Number(Conversions.Clamp(c.Number("level") * max / 100, 0, max)) };
                    if (c.OptionalNumber("transitionMs") is { } ms)
                    {
                        payload["transition"] = Json.Number(Math.Round(ms / 1000, 3));
                    }

                    return (SetTopic, payload);
                });
            }

            Get(feature, property);
        }

        private void MapColorTemperature(EndpointBuilder endpoint, JsonElement feature)
        {
            var property = feature.Str("property") ?? "color_temp";
            var minMired = feature.Num("value_min") ?? 150;
            var maxMired = feature.Num("value_max") ?? 500;
            endpoint.Add(CapabilityIds.Color);
            var minKelvin = Math.Max(1000, Math.Ceiling(1_000_000 / maxMired));
            var maxKelvin = Math.Min(10_000, Math.Floor(1_000_000 / minMired));
            if (minKelvin <= maxKelvin)
            {
                endpoint.Constrain(CapabilityIds.Color, "colorTemperature", new AttributeConstraint(Min: minKelvin, Max: maxKelvin));
            }

            States.Add(new Z2mStateBinding(
                property,
                endpoint.Key,
                CapabilityIds.Color,
                "colorTemperature",
                v => Conversions.Number(v) is { } mired && mired > 0
                    ? StateValue.Number(Conversions.Clamp(Conversions.MiredToKelvin(mired), 1000, 10_000))
                    : null));
            if (Has(feature, Settable))
            {
                Command(endpoint.Key, CapabilityIds.Color, "setColorTemperature", c =>
                    (SetTopic, new JsonObject { [property] = Json.Number(Conversions.Clamp(Conversions.KelvinToMired(c.Number("colorTemperature")), minMired, maxMired)) }));
            }

            Get(feature, property);
        }

        private void MapColor(EndpointBuilder endpoint, JsonElement feature, bool supportsHs)
        {
            var property = feature.Str("property") ?? "color";
            endpoint.Add(CapabilityIds.Color);
            States.Add(new Z2mStateBinding(property, endpoint.Key, CapabilityIds.Color, "hue", v => ColorOf(v) is { } c ? StateValue.Number(c.Hue) : null));
            States.Add(new Z2mStateBinding(property, endpoint.Key, CapabilityIds.Color, "saturation", v => ColorOf(v) is { } c ? StateValue.Number(c.Saturation) : null));
            if (Has(feature, Settable))
            {
                Command(endpoint.Key, CapabilityIds.Color, "setColor", c =>
                {
                    var hue = c.Number("hue");
                    var saturation = c.Number("saturation");
                    if (supportsHs)
                    {
                        return (SetTopic, new JsonObject { [property] = new JsonObject { ["hue"] = Json.Number(hue), ["saturation"] = Json.Number(saturation) } });
                    }

                    var (x, y) = Conversions.HsToXy(hue, saturation);
                    return (SetTopic, new JsonObject { [property] = new JsonObject { ["x"] = x, ["y"] = y } });
                });
            }
        }

        private static (double Hue, double Saturation)? ColorOf(JsonElement color)
        {
            if (color.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (color.Num("hue") is { } hue && color.Num("saturation") is { } saturation)
            {
                return (Conversions.Clamp(hue, 0, 360, 1), Conversions.Clamp(saturation, 0, 100));
            }

            return color.Num("x") is { } x && color.Num("y") is { } y ? Conversions.XyToHs(x, y) : null;
        }

        private void MapCover(JsonElement expose, string key)
        {
            var endpoint = Builder.Place(CapabilityIds.Cover, key, key + "_cover", null);
            _kinds.Add("cover");
            endpoint.Add(CapabilityIds.Cover);
            foreach (var feature in expose.Array("features"))
            {
                var property = feature.Str("property") ?? feature.Str("name") ?? string.Empty;
                switch (feature.Str("name"))
                {
                    case "state":
                        var values = feature.Array("values").Select(Json.AsText).OfType<string>().ToList();
                        States.Add(new Z2mStateBinding(property, endpoint.Key, CapabilityIds.Cover, "coverState", v => CoverState(Json.AsText(v))));
                        if (Has(feature, Settable))
                        {
                            foreach (var (command, value) in new[] { ("open", "OPEN"), ("close", "CLOSE"), ("stop", "STOP") })
                            {
                                if (values.Count == 0 || values.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase)))
                                {
                                    var sent = values.FirstOrDefault(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase)) ?? value;
                                    Command(endpoint.Key, CapabilityIds.Cover, command, _ => (SetTopic, new JsonObject { [property] = sent }));
                                }
                            }
                        }

                        Get(feature, property);
                        break;
                    case "position" when feature.Str("type") == "numeric":
                        States.Add(new Z2mStateBinding(
                            property,
                            endpoint.Key,
                            CapabilityIds.Cover,
                            "position",
                            v => Conversions.Number(v) is { } p ? StateValue.Number(Conversions.Clamp(p, 0, 100)) : null));
                        if (Has(feature, Settable))
                        {
                            Command(endpoint.Key, CapabilityIds.Cover, "setPosition", c => (SetTopic, new JsonObject { [property] = Json.Number(c.Number("position")) }));
                        }

                        Get(feature, property);
                        break;
                    default:
                        MapGeneric(endpoint, feature);
                        break;
                }
            }
        }

        private static StateValue? CoverState(string? value) => value?.ToUpperInvariant() switch
        {
            "OPEN" => StateValue.String("open"),
            "CLOSE" or "CLOSED" => StateValue.String("closed"),
            "OPENING" => StateValue.String("opening"),
            "CLOSING" => StateValue.String("closing"),
            "STOP" or "STOPPED" => StateValue.String("stopped"),
            _ => null,
        };

        private void MapLock(JsonElement expose, string key)
        {
            var endpoint = Builder.Place(CapabilityIds.Lock, key, key + "_lock", null);
            _kinds.Add("lock");
            endpoint.Add(CapabilityIds.Lock);
            var features = expose.Array("features").ToList();
            var hasLockState = features.Any(f => f.Str("name") == "lock_state");
            foreach (var feature in features)
            {
                var property = feature.Str("property") ?? feature.Str("name") ?? string.Empty;
                switch (feature.Str("name"))
                {
                    case "state" when feature.Str("type") == "binary":
                        var locked = feature.Get("value_on") ?? JsonSerializer.SerializeToElement("LOCK");
                        var unlocked = feature.Get("value_off") ?? JsonSerializer.SerializeToElement("UNLOCK");
                        if (!hasLockState)
                        {
                            States.Add(new Z2mStateBinding(property, endpoint.Key, CapabilityIds.Lock, "lockState", v =>
                                Binary(v, locked, unlocked) is { } on ? StateValue.String(on.B == true ? "locked" : "unlocked") : null));
                        }

                        if (Has(feature, Settable))
                        {
                            Command(endpoint.Key, CapabilityIds.Lock, "lock", _ => Payload(property, locked));
                            Command(endpoint.Key, CapabilityIds.Lock, "unlock", _ => Payload(property, unlocked));
                        }

                        Get(feature, property);
                        break;
                    case "lock_state":
                        States.Add(new Z2mStateBinding(property, endpoint.Key, CapabilityIds.Lock, "lockState", v => Json.AsText(v) switch
                        {
                            "locked" => StateValue.String("locked"),
                            "unlocked" => StateValue.String("unlocked"),
                            "not_fully_locked" => StateValue.String("jammed"),
                            null => null,
                            _ => StateValue.String("unknown"),
                        }));
                        break;
                    default:
                        MapGeneric(endpoint, feature);
                        break;
                }
            }
        }

        private void MapClimate(JsonElement expose, string key)
        {
            var endpoint = Builder.Place(CapabilityIds.Thermostat, key, key + "_climate", null);
            _kinds.Add("climate");
            endpoint.Add(CapabilityIds.Thermostat);
            var features = expose.Array("features").ToList();
            var setpoint = _setpoints.Select(name => features.FirstOrDefault(f => f.Str("name") == name)).FirstOrDefault(f => f.ValueKind == JsonValueKind.Object);
            foreach (var feature in features)
            {
                var name = feature.Str("name");
                var property = feature.Str("property") ?? name ?? string.Empty;
                if (name == "system_mode" && feature.Str("type") == "enum")
                {
                    var modes = feature.Array("values").Select(Json.AsText).OfType<string>()
                        .Select(v => (Vendor: v, Mode: ModeOf(v))).Where(m => m.Mode is not null).ToList();
                    if (modes.Count > 0)
                    {
                        endpoint.Constrain(CapabilityIds.Thermostat, "mode", new AttributeConstraint(Values: [.. _modeOrder.Where(m => modes.Any(x => x.Mode == m))]));
                    }

                    States.Add(new Z2mStateBinding(property, endpoint.Key, CapabilityIds.Thermostat, "mode", v => ModeOf(Json.AsText(v)) is { } m ? StateValue.String(m) : null));
                    if (Has(feature, Settable))
                    {
                        Command(endpoint.Key, CapabilityIds.Thermostat, "setMode", c =>
                            modes.FirstOrDefault(m => m.Mode == c.Text("mode")).Vendor is { } vendor ? (SetTopic, new JsonObject { [property] = vendor }) : null);
                    }

                    Get(feature, property);
                }
                else if (setpoint.ValueKind == JsonValueKind.Object && name == setpoint.Str("name"))
                {
                    var unit = feature.Str("unit");
                    var toCelsius = Conversions.ToCanonical("temperature", unit) ?? (v => v);
                    var min = Math.Max(5, toCelsius(feature.Num("value_min") ?? 5));
                    var max = Math.Min(35, toCelsius(feature.Num("value_max") ?? 35));
                    var step = feature.Num("value_step") is { } s && s >= 0.5 && Math.Abs((s / 0.5) - Math.Round(s / 0.5)) < 1e-9 ? s : (double?)null;
                    var (low, high) = (Math.Ceiling(min * 2) / 2, Math.Floor(max * 2) / 2);
                    if (low <= high)
                    {
                        endpoint.Constrain(CapabilityIds.Thermostat, "targetTemperature", new AttributeConstraint(Min: low, Max: high, Step: step));
                    }

                    States.Add(new Z2mStateBinding(property, endpoint.Key, CapabilityIds.Thermostat, "targetTemperature", v =>
                        Conversions.Number(v) is { } t ? StateValue.Number(Math.Round(toCelsius(t), 2)) : null));
                    if (Has(feature, Settable))
                    {
                        var fromCelsius = unit is "°F" or "F" ? (Func<double, double>)(c => Math.Round((c * 9 / 5) + 32, 1)) : c => c;
                        Command(endpoint.Key, CapabilityIds.Thermostat, "setTargetTemperature", c =>
                            (SetTopic, new JsonObject { [property] = Json.Number(fromCelsius(c.Number("targetTemperature"))) }));
                    }

                    Get(feature, property);
                }
                else if (name == "local_temperature" && feature.Str("type") == "numeric")
                {
                    var toCelsius = Conversions.ToCanonical("temperature", feature.Str("unit")) ?? (v => v);
                    States.Add(new Z2mStateBinding(property, endpoint.Key, CapabilityIds.Thermostat, "currentTemperature", v =>
                        Conversions.Number(v) is { } t ? StateValue.Number(Math.Round(toCelsius(t), 2)) : null));
                    Get(feature, property);
                }
                else if (name == "fan_mode" && feature.Str("type") == "enum")
                {
                    var fans = feature.Array("values").Select(Json.AsText).OfType<string>()
                        .Where(v => _fanOrder.Contains(v, StringComparer.Ordinal)).ToList();
                    if (fans.Count > 0)
                    {
                        endpoint.Constrain(CapabilityIds.Thermostat, "fanMode", new AttributeConstraint(Values: [.. _fanOrder.Where(fans.Contains)]));
                    }

                    States.Add(new Z2mStateBinding(property, endpoint.Key, CapabilityIds.Thermostat, "fanMode", v =>
                        Json.AsText(v) is { } f && fans.Contains(f) ? StateValue.String(f) : null));
                    if (Has(feature, Settable))
                    {
                        Command(endpoint.Key, CapabilityIds.Thermostat, "setFanMode", c => (SetTopic, new JsonObject { [property] = c.Text("fanMode") }));
                    }
                }
                else
                {
                    MapGeneric(endpoint, feature);
                }
            }
        }

        private static string? ModeOf(string? vendor) => vendor switch
        {
            "off" or "heat" or "cool" or "auto" or "dry" => vendor,
            "fan_only" => "fan",
            _ => null,
        };

        /// <summary>A top-level generic expose (numeric, binary, enum, text, composite, list) mapped by its name.</summary>
        private void MapStandalone(JsonElement expose, string key)
        {
            if (!Has(expose, Published) || expose.Str("property") is not { } property)
            {
                MapGeneric(Builder.Endpoint(key), expose);
                return;
            }

            var type = expose.Str("type");
            var name = expose.Str("name");
            var fallback = Naming.EndpointKey(property);
            switch (name)
            {
                case "temperature" when type == "numeric" && Conversions.ToCanonical("temperature", expose.Str("unit")) is { } toCelsius:
                    Sensor(CapabilityIds.Temperature, "temperature", v => Math.Round(toCelsius(v), 2));
                    break;
                case "humidity" when type == "numeric":
                    Sensor(CapabilityIds.Humidity, "humidity", v => Math.Clamp(v, 0, 100));
                    break;
                case "battery" when type == "numeric":
                    Sensor(CapabilityIds.Battery, "battery", v => Conversions.Clamp(v, 0, 100));
                    break;
                case "power" when type == "numeric" && Conversions.ToCanonical("power", expose.Str("unit")) is { } toWatt:
                    Sensor(CapabilityIds.Power, "power", toWatt);
                    break;
                case "energy" when type == "numeric" && Conversions.ToCanonical("energy", expose.Str("unit") ?? "kWh") is { } toWh:
                    Sensor(CapabilityIds.Energy, "energy", v => Math.Round(toWh(v), 3));
                    break;
                case "co2" when type == "numeric":
                    Sensor(CapabilityIds.AirQuality, "co2", v => v);
                    break;
                case "pm25" when type == "numeric":
                    Sensor(CapabilityIds.AirQuality, "pm25", v => v);
                    break;
                case "voc" when type == "numeric" && expose.Str("unit") is null or "ppb":
                    Sensor(CapabilityIds.AirQuality, "voc", v => v);
                    break;
                case "occupancy" when type == "binary":
                    Flag(CapabilityIds.Occupancy, "occupied", invert: false);
                    break;
                case "presence" when type == "binary":
                    Flag(CapabilityIds.Presence, "present", invert: false);
                    break;
                case "contact" when type == "binary":
                    // Zigbee2MQTT: contact = true means the magnet is near (closed); the capability reports "open". (Its
                    // expose declares value_on = false, i.e. "on" = open, so the raw boolean is read directly.)
                    Contact();
                    break;
                case "water_leak" when type == "binary":
                    Flag(CapabilityIds.WaterLeak, "leak", invert: false);
                    break;
                case "smoke" when type == "binary":
                    Flag(CapabilityIds.Smoke, "smoke", invert: false);
                    break;
                case "illuminance" or "illuminance_lux" when type == "numeric":
                    Measurement("illuminance", Conversions.Lux, v => v);
                    break;
                case "voltage" when type == "numeric" && Conversions.ToCanonical("voltage", expose.Str("unit")) is { } toVolt:
                    Measurement("voltage", Conversions.Volt, toVolt);
                    break;
                case "current" when type == "numeric" && Conversions.ToCanonical("current", expose.Str("unit")) is { } toAmpere:
                    Measurement("current", Conversions.Ampere, toAmpere);
                    break;
                case "action" when type == "enum":
                    MapAction(expose, key, property);
                    break;
                default:
                    MapGeneric(Builder.Endpoint(key), expose);
                    break;
            }

            void Sensor(string capability, string attribute, Func<double, double> convert)
            {
                if (Free(capability, attribute) is not { } endpoint)
                {
                    MapGeneric(Builder.Endpoint(key), expose);
                    return;
                }

                endpoint.Add(capability);
                _kinds.Add(capability);
                States.Add(new Z2mStateBinding(property, endpoint.Key, capability, attribute, v => Conversions.Number(v) is { } n ? StateValue.Number(convert(n)) : null));
            }

            void Flag(string capability, string attribute, bool invert)
            {
                if (Free(capability, attribute) is not { } endpoint)
                {
                    MapGeneric(Builder.Endpoint(key), expose);
                    return;
                }

                var on = expose.Get("value_on") ?? JsonSerializer.SerializeToElement(true);
                var off = expose.Get("value_off") ?? JsonSerializer.SerializeToElement(false);
                endpoint.Add(capability);
                _kinds.Add(capability);
                States.Add(new Z2mStateBinding(property, endpoint.Key, capability, attribute, v =>
                    Binary(v, on, off) is { } b ? StateValue.Bool(b.B != invert) : null));
            }

            void Contact()
            {
                if (Free(CapabilityIds.Contact, "open") is not { } endpoint)
                {
                    MapGeneric(Builder.Endpoint(key), expose);
                    return;
                }

                endpoint.Add(CapabilityIds.Contact);
                _kinds.Add(CapabilityIds.Contact);
                States.Add(new Z2mStateBinding(property, endpoint.Key, CapabilityIds.Contact, "open", v => v.ValueKind switch
                {
                    JsonValueKind.True => StateValue.False,
                    JsonValueKind.False => StateValue.True,
                    _ => null,
                }));
            }

            // The expose's endpoint while that attribute is unbound there, else an endpoint named after the property.
            EndpointBuilder? Free(string capability, string attribute) =>
                !Bound(key, capability, attribute) ? Builder.Endpoint(key)
                : !Bound(fallback, capability, attribute) ? Builder.Endpoint(fallback, expose.Str("label"))
                : null;

            void Measurement(string attributeKey, string unit, Func<double, double> convert)
            {
                var endpoint = Builder.Endpoint(key);
                var attribute = endpoint.AddGeneric(attributeKey, new NumberSchema(Unit: unit), StateClass.Measurement, Category(expose), recorded: true);
                _kinds.Add("sensor");
                States.Add(new Z2mStateBinding(property, endpoint.Key, CapabilityIds.Generic, attribute, v =>
                    Conversions.Number(v) is { } n ? StateValue.Number(Math.Round(convert(n), 4)) : null));
            }
        }

        /// <summary>
        /// <c>action</c>: single / double / hold become <c>button.press</c> events (single, double, long); other values
        /// (release, triple, scene buttons) become events of a generic momentary <c>action</c> attribute.
        /// </summary>
        private void MapAction(JsonElement expose, string key, string property)
        {
            var values = expose.Array("values").Select(Json.AsText).OfType<string>().Where(v => v.Length > 0).ToList();
            var endpoint = Builder.Endpoint(key);
            _kinds.Add("action");
            if (values.Any(v => ButtonEvent(v) is not null))
            {
                endpoint.Add(CapabilityIds.Button);
            }

            var others = values.Where(v => ButtonEvent(v) is null).ToList();
            var genericKey = others.Count > 0 || values.Count == 0
                ? endpoint.AddGeneric("action", new MomentarySchema { Events = others })
                : null;
            Actions.Add(new Z2mActionBinding(property, endpoint.Key, value =>
                ButtonEvent(value) is { } press && endpoint.Has(CapabilityIds.Button)
                    ? (CapabilityIds.Button, "press", press)
                    : genericKey is not null ? (CapabilityIds.Generic, genericKey, value) : null));
        }

        private static string? ButtonEvent(string value) => value switch
        {
            "single" => "single",
            "double" => "double",
            "hold" or "long" or "long_press" => "long",
            _ => null,
        };

        /// <summary>Any expose without a catalogue mapping → a generic attribute (and setter when settable).</summary>
        private void MapGeneric(EndpointBuilder endpoint, JsonElement expose)
        {
            var property = expose.Str("property");
            var name = expose.Str("name") ?? property;
            if (property is null || name is null)
            {
                return;
            }

            var published = Has(expose, Published);
            var settable = Has(expose, Settable);
            if (!published && !settable)
            {
                return;
            }

            var key = Naming.CamelCase(name);
            var category = Category(expose);
            switch (expose.Str("type"))
            {
                case "numeric":
                {
                    var unit = expose.Str("unit");
                    var schema = settable
                        ? new NumberSchema(expose.Num("value_min"), expose.Num("value_max"), expose.Num("value_step"), unit)
                        : new NumberSchema(Unit: unit);
                    Generic(schema, published && !settable ? StateClass.Measurement : StateClass.None, v => Conversions.Number(v) is { } n ? StateValue.Number(n) : null, v => v);
                    break;
                }

                case "binary":
                {
                    var on = expose.Get("value_on") ?? JsonSerializer.SerializeToElement(true);
                    var off = expose.Get("value_off") ?? JsonSerializer.SerializeToElement(false);
                    Generic(BoolSchema.Instance, StateClass.None, v => Binary(v, on, off), v => v.ValueKind == JsonValueKind.True ? on : off);
                    break;
                }

                case "enum":
                {
                    var values = expose.Array("values").Where(v => Json.AsText(v) is { Length: > 0 }).ToList();
                    var names = values.Select(v => Json.AsText(v)!).Distinct(StringComparer.Ordinal).ToList();
                    ValueSchema schema = names.Count > 0 ? new EnumSchema(names) : new StringSchema(255);
                    Generic(
                        schema,
                        StateClass.None,
                        v => Json.AsText(v) is { } text && (names.Count == 0 || names.Contains(text)) ? StateValue.String(text) : null,
                        v => values.FirstOrDefault(x => Json.AsText(x) == v.GetString()) is { ValueKind: not JsonValueKind.Undefined } original ? original : v);
                    break;
                }

                case "text":
                    Generic(new StringSchema(255), StateClass.None, v => v.ValueKind == JsonValueKind.String && v.GetString()!.Length <= 255 ? StateValue.String(v.GetString()!) : null, v => v);
                    break;
                default:
                    // composite, list and unknown types: read-only JSON.
                    if (published)
                    {
                        var attribute = endpoint.AddGeneric(key, JsonValueSchema.Instance, category: category);
                        States.Add(new Z2mStateBinding(property, endpoint.Key, CapabilityIds.Generic, attribute, v =>
                            v.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? StateValue.Json(v) : StateValue.FromJson(v)));
                    }

                    break;
            }

            void Generic(ValueSchema schema, StateClass stateClass, Func<JsonElement, StateValue?> read, Func<JsonElement, JsonElement> write)
            {
                if (published)
                {
                    var attribute = endpoint.AddGeneric(key, schema, stateClass, category, recorded: stateClass == StateClass.Measurement, writable: settable);
                    States.Add(new Z2mStateBinding(property, endpoint.Key, CapabilityIds.Generic, attribute, read));
                    if (settable)
                    {
                        Command(endpoint.Key, CapabilityIds.Generic, endpoint.SetterOf(attribute), c => Payload(property, write(c.Argument(attribute))));
                    }

                    Get(expose, property, catalogue: false);
                }
                else
                {
                    // Write-only (e.g. identify, effect): a command without an attribute.
                    var argument = key;
                    var command = endpoint.AddGenericCommand("set" + char.ToUpperInvariant(key[0]) + key[1..], [new ArgumentDefinition(argument, schema, Required: true)], ConfirmationMode.AckIsFinal);
                    Command(endpoint.Key, CapabilityIds.Generic, command, c => Payload(property, write(c.Argument(argument))));
                }
            }
        }

        private static AttributeCategory Category(JsonElement expose) =>
            expose.Str("category") is "diagnostic" or "config" || expose.Str("name") is "linkquality" or "battery_low" or "device_temperature" or "update"
                ? AttributeCategory.Diagnostic
                : AttributeCategory.Primary;

        private void Command(string endpoint, string capability, string command, Func<ValidatedCommand, (string Topic, JsonObject Payload)?> build) =>
            Commands[(endpoint, capability, command)] = c => build(c);

        private bool Bound(string endpoint, string capability, string attribute) =>
            States.Any(s => s.Endpoint == endpoint && s.Capability == capability && s.Attribute == attribute);

        private (string Topic, JsonObject Payload) Payload(string property, JsonElement value) => (SetTopic, new JsonObject { [property] = Json.Node(value) });

        private void Get(JsonElement feature, string property, bool catalogue = true)
        {
            if (catalogue && Has(feature, GettableBit) && !Gettable.Contains(property))
            {
                Gettable.Add(property);
            }
        }

        /// <summary>A binary value: <paramref name="on"/> → true, <paramref name="off"/> → false, anything else → nothing.</summary>
        private static StateValue? Binary(JsonElement value, JsonElement on, JsonElement off) =>
            JsonElement.DeepEquals(value, on) ? StateValue.True
            : JsonElement.DeepEquals(value, off) ? StateValue.False
            : null;
    }
}

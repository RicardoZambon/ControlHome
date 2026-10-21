using System.Text.Json;
using System.Text.Json.Nodes;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.Mqtt.Model;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;

namespace ControlHome.Connectors.Mqtt.HomeAssistant;

/// <summary>
/// Maps the Home Assistant discovery entities of one device to a <see cref="HaDevice"/>. Components: <c>switch</c> →
/// switch; <c>light</c> (default and JSON schema) → switch/level/color; <c>sensor</c> and <c>binary_sensor</c> by
/// <c>device_class</c> → temperature, humidity, power, energy, battery, air quality, contact, motion, occupancy,
/// presence, smoke, water leak (others → generic); <c>cover</c> → cover; <c>lock</c> → lock; <c>climate</c> →
/// thermostat; <c>button</c> → a generic <c>press…</c> command. Entities are taken by component (actuators first), in the order of their configs; the first entity of a capability goes to the
/// <c>main</c> endpoint, further ones to an endpoint named after the entity. Templates outside the supported subset
/// (<see cref="ValueTemplate"/>) do not fail the device: the attribute is marked unsupported (a diagnostic
/// <c>unsupportedTemplates</c> attribute lists them, and a sensor's raw payload is kept in a diagnostic attribute).
/// </summary>
internal static class HaDeviceMapper
{
    public const string UnsupportedAttribute = "unsupportedTemplates";

    private static readonly string[] _order = ["lock", "cover", "climate", "light", "switch", "sensor", "binary_sensor", "button"];
    private static readonly string[] _modeOrder = ["off", "cool", "heat", "auto", "dry", "fan"];
    private static readonly string[] _fanOrder = ["auto", "quiet", "low", "medium", "high", "turbo"];

    public static HaDevice Map(string externalId, JsonElement? device, IReadOnlyList<HaEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        var context = new Context();
        foreach (var entity in entities
            .Where(e => _order.Contains(e.Component, StringComparer.Ordinal))
            .OrderBy(e => Array.IndexOf(_order, e.Component)))
        {
            context.Map(entity);
        }

        var main = context.Builder.Endpoint(DeviceBuilder.Main);
        var initial = new List<MqttReading>();
        if (context.Unsupported.Count > 0)
        {
            var key = main.AddGeneric(UnsupportedAttribute, JsonValueSchema.Instance, category: AttributeCategory.Diagnostic);
            var list = new JsonObject();
            foreach (var (name, template) in context.Unsupported)
            {
                list[name] = template;
            }

            initial.Add(new MqttReading.State(DeviceBuilder.Main, CapabilityIds.Generic, key, StateValue.Json(JsonSerializer.SerializeToElement(list))));
        }

        var info = device ?? default;
        var firstName = entities.Select(e => e.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        var connections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var connection in info.Array("connections"))
        {
            if (connection.ValueKind == JsonValueKind.Array && connection.GetArrayLength() == 2
                && Json.AsText(connection[0]) is { Length: > 0 } type && Json.AsText(connection[1]) is { Length: > 0 } value)
            {
                connections.TryAdd(type, value);
            }
        }

        if (info.Text("serial_number") is { Length: > 0 } serial)
        {
            connections.TryAdd("serial", serial);
        }

        var discovered = new DiscoveredDevice(
            externalId,
            info.Str("name") ?? firstName ?? externalId[HaDiscoveryParser.ExternalIdPrefix.Length..],
            context.DeviceClass,
            info.Str("manufacturer"),
            info.Str("model") ?? info.Str("model_id"),
            info.Text("sw_version"),
            info.Str("via_device") is { Length: > 0 } via ? HaDiscoveryParser.ExternalIdPrefix + via : null,
            connections,
            info.Str("suggested_area"),
            context.Builder.Build());
        return new HaDevice(
            discovered,
            context.Readers.ToDictionary(r => r.Key, r => (IReadOnlyList<Func<string, IEnumerable<MqttReading>>>)r.Value, StringComparer.Ordinal),
            context.Commands,
            context.HasAvailability,
            initial);
    }

    /// <summary>The last on/off state seen, so <c>toggle</c> can pick the opposite payload.</summary>
    private sealed class Memory
    {
        private int _state = -1;

        public bool? On
        {
            get => Volatile.Read(ref _state) switch { 1 => true, 0 => false, _ => null };
            set => Volatile.Write(ref _state, value switch { true => 1, false => 0, null => -1 });
        }
    }

    private sealed class Context
    {
        private readonly HashSet<(string Topic, string Available, string NotAvailable, string? Template)> _availability = [];
        private string? _deviceClass;
        private int _deviceClassRank = int.MaxValue;

        public DeviceBuilder Builder { get; } = new();

        public Dictionary<string, List<Func<string, IEnumerable<MqttReading>>>> Readers { get; } = new(StringComparer.Ordinal);

        public Dictionary<(string Endpoint, string Capability, string Command), Func<ValidatedCommand, CommandPlan?>> Commands { get; } = [];

        public List<(string Name, string Template)> Unsupported { get; } = [];

        public bool HasAvailability { get; private set; }

        public string? DeviceClass => _deviceClass;

        private HashSet<(string Endpoint, string Capability, string Attribute)> Bound { get; } = [];

        public void Map(HaEntity entity)
        {
            MapAvailability(entity);
            switch (entity.Component)
            {
                case "sensor":
                    MapSensor(entity);
                    break;
                case "binary_sensor":
                    MapBinarySensor(entity);
                    break;
                case "switch":
                    MapSwitch(entity);
                    break;
                case "light":
                    MapLight(entity);
                    break;
                case "cover":
                    MapCover(entity);
                    break;
                case "lock":
                    MapLock(entity);
                    break;
                case "climate":
                    MapClimate(entity);
                    break;
                case "button":
                    MapButton(entity);
                    break;
            }
        }

        // ---- availability -------------------------------------------------------------------------------------------

        private void MapAvailability(HaEntity entity)
        {
            var config = entity.Config;
            var entries = new List<(string Topic, string Available, string NotAvailable, string? Template)>();
            if (config.Str("availability_topic") is { Length: > 0 } topic)
            {
                entries.Add((topic, config.Text("payload_available") ?? "online", config.Text("payload_not_available") ?? "offline", config.Str("availability_template")));
            }

            foreach (var entry in config.Array("availability"))
            {
                if (entry.Str("topic") is { Length: > 0 } entryTopic)
                {
                    entries.Add((entryTopic, entry.Text("payload_available") ?? "online", entry.Text("payload_not_available") ?? "offline", entry.Str("value_template")));
                }
            }

            foreach (var (availabilityTopic, available, notAvailable, template) in entries)
            {
                // The entities of a device usually share one availability topic: read it once.
                if (!_availability.Add((availabilityTopic, available, notAvailable, template)))
                {
                    continue;
                }

                if (!ValueTemplate.TryParse(template, out var parsed))
                {
                    Unsupported.Add(($"{entity.Key}.availability_template", template!));
                    continue;
                }

                HasAvailability = true;
                Read(availabilityTopic, payload => parsed.Evaluate(payload)?.Text is { } text
                    ? text == available ? [new MqttReading.Reachability(Availability.Online)]
                    : text == notAvailable ? [new MqttReading.Reachability(Availability.Offline)]
                    : []
                    : []);
            }
        }

        // ---- sensors -----------------------------------------------------------------------------------------------

        private void MapSensor(HaEntity entity)
        {
            var config = entity.Config;
            if (config.Str("state_topic") is not { Length: > 0 } stateTopic)
            {
                return;
            }

            if (!Template(entity, "value_template", out var template))
            {
                Raw(entity, stateTopic);
                return;
            }

            var deviceClass = config.Str("device_class");
            var unit = config.Str("unit_of_measurement");
            var diagnostic = config.Str("entity_category") is "diagnostic" or "config";
            (string Capability, string Attribute, Func<double, double> Convert)? catalogue = deviceClass switch
            {
                "temperature" when !diagnostic && Conversions.ToCanonical("temperature", unit) is { } c => (CapabilityIds.Temperature, "temperature", v => Math.Round(c(v), 2)),
                "humidity" when !diagnostic => (CapabilityIds.Humidity, "humidity", v => Math.Clamp(v, 0, 100)),
                "power" when !diagnostic && Conversions.ToCanonical("power", unit) is { } w => (CapabilityIds.Power, "power", w),
                "energy" when !diagnostic && Conversions.ToCanonical("energy", unit) is { } wh => (CapabilityIds.Energy, "energy", v => Math.Round(wh(v), 3)),
                "battery" => (CapabilityIds.Battery, "battery", v => Conversions.Clamp(v, 0, 100)),
                "pm25" when !diagnostic => (CapabilityIds.AirQuality, "pm25", v => v),
                "carbon_dioxide" when !diagnostic => (CapabilityIds.AirQuality, "co2", v => v),
                "volatile_organic_compounds_parts" when !diagnostic && unit is null or "ppb" or "ppm" =>
                    (CapabilityIds.AirQuality, "voc", v => unit == "ppm" ? v * 1000 : v),
                _ => null,
            };

            if (catalogue is { } mapped && Free(entity, mapped.Capability, mapped.Attribute) is { } endpoint)
            {
                endpoint.Add(mapped.Capability);
                SetDeviceClass(DeviceClasses.Sensor, 100);
                Read(stateTopic, payload => template.Evaluate(payload)?.Number is { } number
                    ? [new MqttReading.State(endpoint.Key, mapped.Capability, mapped.Attribute, StateValue.Number(mapped.Convert(number)))]
                    : []);
                return;
            }

            // Everything else: a generic number (with a unit or state class) or text.
            Func<double, double>? convert = deviceClass switch
            {
                "voltage" => Conversions.ToCanonical("voltage", unit),
                "current" => Conversions.ToCanonical("current", unit),
                _ => null,
            };
            var canonicalUnit = deviceClass switch
            {
                "voltage" when convert is not null => Conversions.Volt,
                "current" when convert is not null => Conversions.Ampere,
                "illuminance" => unit ?? Conversions.Lux,
                _ => unit,
            };
            convert ??= v => v;
            var stateClass = config.Str("state_class") switch
            {
                "measurement" => StateClass.Measurement,
                "total" => StateClass.Total,
                "total_increasing" => StateClass.TotalIncreasing,
                _ => deviceClass is "illuminance" or "voltage" or "current" or "pressure" ? StateClass.Measurement : StateClass.None,
            };
            var numeric = canonicalUnit is not null || stateClass != StateClass.None;
            var main = Builder.Endpoint(DeviceBuilder.Main);
            var category = diagnostic ? AttributeCategory.Diagnostic : AttributeCategory.Primary;
            var key = numeric
                ? main.AddGeneric(EntityKey(entity), new NumberSchema(Unit: canonicalUnit), stateClass, category, recorded: stateClass != StateClass.None)
                : main.AddGeneric(EntityKey(entity), new StringSchema(255), category: category);
            SetDeviceClass(DeviceClasses.Sensor, 100);
            Read(stateTopic, payload => template.Evaluate(payload) is not { } value
                ? []
                : numeric
                    ? value.Number is { } number ? [new MqttReading.State(DeviceBuilder.Main, CapabilityIds.Generic, key, StateValue.Number(convert(number)))] : []
                    : value.Text.Length <= 255 ? [new MqttReading.State(DeviceBuilder.Main, CapabilityIds.Generic, key, StateValue.String(value.Text))] : []);
        }

        private void MapBinarySensor(HaEntity entity)
        {
            var config = entity.Config;
            if (config.Str("state_topic") is not { Length: > 0 } stateTopic)
            {
                return;
            }

            if (!Template(entity, "value_template", out var template))
            {
                Raw(entity, stateTopic);
                return;
            }

            var on = config.Text("payload_on") ?? "ON";
            var off = config.Text("payload_off") ?? "OFF";
            var deviceClass = config.Str("device_class");
            var diagnostic = config.Str("entity_category") is "diagnostic" or "config";
            (string Capability, string Attribute)? catalogue = diagnostic ? null : deviceClass switch
            {
                "door" or "window" or "garage_door" or "opening" => (CapabilityIds.Contact, "open"),
                "motion" or "moving" => (CapabilityIds.Motion, "motion"),
                "occupancy" => (CapabilityIds.Occupancy, "occupied"),
                "presence" => (CapabilityIds.Presence, "present"),
                "smoke" => (CapabilityIds.Smoke, "smoke"),
                "moisture" => (CapabilityIds.WaterLeak, "leak"),
                _ => null,
            };

            var (endpointKey, capability, attribute) = (DeviceBuilder.Main, CapabilityIds.Generic, string.Empty);
            if (catalogue is { } mapped && Free(entity, mapped.Capability, mapped.Attribute) is { } endpoint)
            {
                endpoint.Add(mapped.Capability);
                (endpointKey, capability, attribute) = (endpoint.Key, mapped.Capability, mapped.Attribute);
                SetDeviceClass(deviceClass is "door" or "garage_door" ? DeviceClasses.Door : deviceClass == "window" ? DeviceClasses.Window : DeviceClasses.Sensor, 90);
            }
            else
            {
                attribute = Builder.Endpoint(DeviceBuilder.Main).AddGeneric(
                    EntityKey(entity),
                    BoolSchema.Instance,
                    category: diagnostic || deviceClass is "battery" or "problem" or "connectivity" ? AttributeCategory.Diagnostic : AttributeCategory.Primary);
                SetDeviceClass(DeviceClasses.Sensor, 100);
            }

            Read(stateTopic, payload => template.Evaluate(payload) is { } value && OnOff(value, on, off) is { } flag
                ? [new MqttReading.State(endpointKey, capability, attribute, StateValue.Bool(flag))]
                : []);
        }

        // ---- actuators ---------------------------------------------------------------------------------------------

        private void MapSwitch(HaEntity entity)
        {
            var config = entity.Config;
            var endpoint = Place(entity, CapabilityIds.Switch);
            endpoint.Add(CapabilityIds.Switch);
            SetDeviceClass(config.Str("device_class") == "outlet" ? DeviceClasses.Outlet : DeviceClasses.Switch, 50);
            var on = config.Text("payload_on") ?? "ON";
            var off = config.Text("payload_off") ?? "OFF";
            OnOffEntity(entity, endpoint.Key, on, off, config.Text("state_on") ?? on, config.Text("state_off") ?? off, "value_template");
        }

        /// <summary>The on/off part shared by switches and default-schema lights.</summary>
        private void OnOffEntity(HaEntity entity, string endpoint, string on, string off, string stateOn, string stateOff, string templateKey)
        {
            var config = entity.Config;
            var memory = new Memory();
            var stateTopic = config.Str("state_topic");
            var optimistic = config.Bool("optimistic") ?? stateTopic is null;
            if (stateTopic is not null && Template(entity, templateKey, out var template))
            {
                Read(stateTopic, payload =>
                {
                    if (template.Evaluate(payload) is { } value && OnOff(value, stateOn, stateOff) is { } flag)
                    {
                        memory.On = flag;
                        return [new MqttReading.State(endpoint, CapabilityIds.Switch, "on", StateValue.Bool(flag))];
                    }

                    return [];
                });
            }

            if (config.Str("command_topic") is not { Length: > 0 } commandTopic || !CommandTemplateOf(entity, "command_template", out var command))
            {
                return;
            }

            CommandPlan? Send(bool turnOn)
            {
                if (command.Render(turnOn ? on : off) is not { } payload)
                {
                    return null;
                }

                if (optimistic)
                {
                    memory.On = turnOn;
                }

                return new CommandPlan(
                    [new MqttPublish(commandTopic, payload, Retained(config))],
                    optimistic ? [new MqttReading.State(endpoint, CapabilityIds.Switch, "on", StateValue.Bool(turnOn))] : []);
            }

            Commands[(endpoint, CapabilityIds.Switch, "turnOn")] = _ => Send(true);
            Commands[(endpoint, CapabilityIds.Switch, "turnOff")] = _ => Send(false);
            Commands[(endpoint, CapabilityIds.Switch, "toggle")] = _ => Send(memory.On != true);
        }

        private void MapLight(HaEntity entity)
        {
            var config = entity.Config;
            var schema = config.Str("schema") ?? "default";
            if (schema == "template")
            {
                Unsupported.Add(($"{entity.Key}.schema", "template"));
                return;
            }

            var endpoint = Place(entity, CapabilityIds.Switch);
            endpoint.Add(CapabilityIds.Switch);
            SetDeviceClass(DeviceClasses.Light, 40);
            if (schema == "json")
            {
                MapJsonLight(entity, endpoint);
                return;
            }

            var on = config.Text("payload_on") ?? "ON";
            var off = config.Text("payload_off") ?? "OFF";
            OnOffEntity(entity, endpoint.Key, on, off, on, off, "state_value_template");
            var optimistic = config.Bool("optimistic") ?? config.Str("state_topic") is null;

            // Brightness: brightness_scale (default 255) ↔ level 0–100.
            var scale = config.Num("brightness_scale") is { } s && s > 0 ? s : 255;
            if (config.Str("brightness_state_topic") is { Length: > 0 } brightnessState && Template(entity, "brightness_value_template", out var brightnessTemplate))
            {
                endpoint.Add(CapabilityIds.Level);
                Read(brightnessState, payload => brightnessTemplate.Evaluate(payload)?.Number is { } b
                    ? [new MqttReading.State(endpoint.Key, CapabilityIds.Level, "level", StateValue.Number(Conversions.Clamp(b * 100 / scale, 0, 100)))]
                    : []);
            }

            if (config.Str("brightness_command_topic") is { Length: > 0 } brightnessCommand && CommandTemplateOf(entity, "brightness_command_template", out var brightnessCommandTemplate))
            {
                endpoint.Add(CapabilityIds.Level);
                Commands[(endpoint.Key, CapabilityIds.Level, "setLevel")] = c =>
                {
                    var level = c.Number("level");
                    return brightnessCommandTemplate.Render(Conversions.Format(Conversions.Clamp(level * scale / 100, 0, scale))) is { } payload
                        ? new CommandPlan(
                            [new MqttPublish(brightnessCommand, payload, Retained(config))],
                            optimistic ? [new MqttReading.State(endpoint.Key, CapabilityIds.Level, "level", StateValue.Number(level))] : [])
                        : null;
                };
            }

            // Color temperature: mireds, or kelvin with color_temp_kelvin.
            var kelvin = config.Bool("color_temp_kelvin") == true;
            if (config.Str("color_temp_state_topic") is { Length: > 0 } ctState && Template(entity, "color_temp_value_template", out var ctTemplate))
            {
                endpoint.Add(CapabilityIds.Color);
                Read(ctState, payload => ctTemplate.Evaluate(payload)?.Number is { } v && v > 0
                    ? [new MqttReading.State(endpoint.Key, CapabilityIds.Color, "colorTemperature", StateValue.Number(Conversions.Clamp(kelvin ? v : Conversions.MiredToKelvin(v), 1000, 10_000)))]
                    : []);
            }

            if (config.Str("color_temp_command_topic") is { Length: > 0 } ctCommand && CommandTemplateOf(entity, "color_temp_command_template", out var ctCommandTemplate))
            {
                endpoint.Add(CapabilityIds.Color);
                ConstrainColorTemperature(endpoint, config, kelvin);
                Commands[(endpoint.Key, CapabilityIds.Color, "setColorTemperature")] = c =>
                {
                    var k = c.Number("colorTemperature");
                    return ctCommandTemplate.Render(Conversions.Format(kelvin ? k : Conversions.KelvinToMired(k))) is { } payload
                        ? new CommandPlan([new MqttPublish(ctCommand, payload, Retained(config))], [])
                        : null;
                };
            }

            // Color: "h,s" (hs topics) or "x,y" (xy topics).
            foreach (var (prefix, isHs) in new[] { ("hs", true), ("xy", false) })
            {
                if (config.Str($"{prefix}_state_topic") is { Length: > 0 } colorState && Template(entity, $"{prefix}_value_template", out var colorTemplate))
                {
                    endpoint.Add(CapabilityIds.Color);
                    var readHs = isHs;
                    Read(colorState, payload => colorTemplate.Evaluate(payload) is { } value && Pair(value.Text) is { } pair
                        ? HueSaturation(endpoint.Key, readHs ? (Conversions.Clamp(pair.A, 0, 360, 1), Conversions.Clamp(pair.B, 0, 100)) : Conversions.XyToHs(pair.A, pair.B))
                        : []);
                }

                if (config.Str($"{prefix}_command_topic") is { Length: > 0 } colorCommand
                    && !Commands.ContainsKey((endpoint.Key, CapabilityIds.Color, "setColor"))
                    && CommandTemplateOf(entity, $"{prefix}_command_template", out var colorCommandTemplate))
                {
                    endpoint.Add(CapabilityIds.Color);
                    var sendHs = isHs;
                    Commands[(endpoint.Key, CapabilityIds.Color, "setColor")] = c =>
                    {
                        var (hue, saturation) = (c.Number("hue"), c.Number("saturation"));
                        var text = sendHs
                            ? $"{Conversions.Format(hue)},{Conversions.Format(saturation)}"
                            : Conversions.HsToXy(hue, saturation) is var (x, y) ? $"{Conversions.Format(x)},{Conversions.Format(y)}" : string.Empty;
                        return colorCommandTemplate.Render(text) is { } payload
                            ? new CommandPlan([new MqttPublish(colorCommand, payload, Retained(config))], [])
                            : null;
                    };
                }
            }
        }

        private void MapJsonLight(HaEntity entity, EndpointBuilder endpoint)
        {
            var config = entity.Config;
            var modes = config.Array("supported_color_modes").Select(Json.AsText).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var brightness = config.Bool("brightness") == true || modes.Overlaps(["brightness", "color_temp", "hs", "xy", "rgb", "rgbw", "rgbww", "white"]);
            var colorTemp = config.Bool("color_temp") == true || modes.Contains("color_temp");
            var hs = config.Bool("hs") == true || modes.Contains("hs");
            var xy = config.Bool("xy") == true || modes.Contains("xy");
            var rgb = config.Bool("rgb") == true || modes.Overlaps(["rgb", "rgbw", "rgbww"]);
            var kelvin = config.Bool("color_temp_kelvin") == true;
            var scale = config.Num("brightness_scale") is { } s && s > 0 ? s : 255;
            var memory = new Memory();
            var stateTopic = config.Str("state_topic");
            var optimistic = config.Bool("optimistic") ?? stateTopic is null;
            if (brightness)
            {
                endpoint.Add(CapabilityIds.Level);
            }

            if (colorTemp || hs || xy || rgb)
            {
                endpoint.Add(CapabilityIds.Color);
            }

            if (colorTemp)
            {
                ConstrainColorTemperature(endpoint, config, kelvin);
            }

            if (stateTopic is not null)
            {
                Read(stateTopic, payload =>
                {
                    if (Json.TryParse(payload) is not { ValueKind: JsonValueKind.Object } state)
                    {
                        return [];
                    }

                    var readings = new List<MqttReading>();
                    if (state.Str("state") is { } onOff && OnOff(new TemplateValue(onOff, null), "ON", "OFF") is { } flag)
                    {
                        memory.On = flag;
                        readings.Add(new MqttReading.State(endpoint.Key, CapabilityIds.Switch, "on", StateValue.Bool(flag)));
                    }

                    if (brightness && state.Num("brightness") is { } b)
                    {
                        readings.Add(new MqttReading.State(endpoint.Key, CapabilityIds.Level, "level", StateValue.Number(Conversions.Clamp(b * 100 / scale, 0, 100))));
                    }

                    if (colorTemp && state.Num("color_temp") is { } ct && ct > 0)
                    {
                        readings.Add(new MqttReading.State(endpoint.Key, CapabilityIds.Color, "colorTemperature", StateValue.Number(Conversions.Clamp(kelvin ? ct : Conversions.MiredToKelvin(ct), 1000, 10_000))));
                    }

                    if (state.Get("color") is { ValueKind: JsonValueKind.Object } color && state.Str("color_mode") is not ("color_temp" or "white" or "brightness" or "onoff"))
                    {
                        (double, double)? hsValue =
                            color.Num("h") is { } h && color.Num("s") is { } sat ? (Conversions.Clamp(h, 0, 360, 1), Conversions.Clamp(sat, 0, 100))
                            : color.Num("x") is { } x && color.Num("y") is { } y ? Conversions.XyToHs(x, y)
                            : color.Num("r") is { } r && color.Num("g") is { } g && color.Num("b") is { } bl ? RgbToHs(r, g, bl)
                            : null;
                        if (hsValue is { } value)
                        {
                            readings.AddRange(HueSaturation(endpoint.Key, value));
                        }
                    }

                    return readings;
                });
            }

            if (config.Str("command_topic") is not { Length: > 0 } commandTopic)
            {
                return;
            }

            CommandPlan Send(JsonObject payload, IReadOnlyList<MqttReading> assumed) =>
                new([new MqttPublish(commandTopic, Json.Serialize(payload), Retained(config))], optimistic ? assumed : []);

            Commands[(endpoint.Key, CapabilityIds.Switch, "turnOn")] = _ => Send(new JsonObject { ["state"] = "ON" }, [On(true)]);
            Commands[(endpoint.Key, CapabilityIds.Switch, "turnOff")] = _ => Send(new JsonObject { ["state"] = "OFF" }, [On(false)]);
            Commands[(endpoint.Key, CapabilityIds.Switch, "toggle")] = _ =>
                memory.On == true ? Send(new JsonObject { ["state"] = "OFF" }, [On(false)]) : Send(new JsonObject { ["state"] = "ON" }, [On(true)]);
            if (brightness)
            {
                Commands[(endpoint.Key, CapabilityIds.Level, "setLevel")] = c =>
                {
                    var payload = new JsonObject { ["state"] = "ON", ["brightness"] = Json.Number(Conversions.Clamp(c.Number("level") * scale / 100, 0, scale)) };
                    if (c.OptionalNumber("transitionMs") is { } ms)
                    {
                        payload["transition"] = Json.Number(Math.Round(ms / 1000, 3));
                    }

                    return Send(payload, [On(true), new MqttReading.State(endpoint.Key, CapabilityIds.Level, "level", StateValue.Number(c.Number("level")))]);
                };
            }

            if (colorTemp)
            {
                Commands[(endpoint.Key, CapabilityIds.Color, "setColorTemperature")] = c =>
                {
                    var k = c.Number("colorTemperature");
                    return Send(new JsonObject { ["state"] = "ON", ["color_temp"] = Json.Number(kelvin ? k : Conversions.KelvinToMired(k)) }, [On(true)]);
                };
            }

            if (hs || xy || rgb)
            {
                Commands[(endpoint.Key, CapabilityIds.Color, "setColor")] = c =>
                {
                    var (hue, saturation) = (c.Number("hue"), c.Number("saturation"));
                    JsonObject color;
                    if (hs)
                    {
                        color = new JsonObject { ["h"] = Json.Number(hue), ["s"] = Json.Number(saturation) };
                    }
                    else if (xy)
                    {
                        var (x, y) = Conversions.HsToXy(hue, saturation);
                        color = new JsonObject { ["x"] = x, ["y"] = y };
                    }
                    else
                    {
                        var (r, g, b) = HsToRgb(hue, saturation);
                        color = new JsonObject { ["r"] = r, ["g"] = g, ["b"] = b };
                    }

                    return Send(new JsonObject { ["state"] = "ON", ["color"] = color }, [On(true)]);
                };
            }

            MqttReading On(bool on) => new MqttReading.State(endpoint.Key, CapabilityIds.Switch, "on", StateValue.Bool(on));
        }

        private void MapCover(HaEntity entity)
        {
            var config = entity.Config;
            var endpoint = Place(entity, CapabilityIds.Cover);
            endpoint.Add(CapabilityIds.Cover);
            SetDeviceClass(config.Str("device_class") switch
            {
                "garage" => DeviceClasses.Garage,
                "gate" => DeviceClasses.Gate,
                "curtain" => DeviceClasses.Curtain,
                "door" => DeviceClasses.Door,
                "window" => DeviceClasses.Window,
                _ => DeviceClasses.Blind,
            }, 20);

            if (config.Str("state_topic") is { Length: > 0 } stateTopic && Template(entity, "value_template", out var template))
            {
                var states = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [config.Text("state_open") ?? "open"] = "open",
                    [config.Text("state_opening") ?? "opening"] = "opening",
                    [config.Text("state_closed") ?? "closed"] = "closed",
                    [config.Text("state_closing") ?? "closing"] = "closing",
                    [config.Text("state_stopped") ?? "stopped"] = "stopped",
                };
                Read(stateTopic, payload => template.Evaluate(payload)?.Text is { } text && states.TryGetValue(text, out var state)
                    ? [new MqttReading.State(endpoint.Key, CapabilityIds.Cover, "coverState", StateValue.String(state))]
                    : []);
            }

            var open = config.Num("position_open") ?? 100;
            var closed = config.Num("position_closed") ?? 0;
            if (config.Str("position_topic") is { Length: > 0 } positionTopic && Template(entity, "position_template", out var positionTemplate) && open != closed)
            {
                Read(positionTopic, payload => positionTemplate.Evaluate(payload)?.Number is { } raw
                    ? [new MqttReading.State(endpoint.Key, CapabilityIds.Cover, "position", StateValue.Number(Conversions.Clamp((raw - closed) * 100 / (open - closed), 0, 100)))]
                    : []);
            }

            if (config.Str("command_topic") is { Length: > 0 } commandTopic)
            {
                foreach (var (command, key, fallback) in new[] { ("open", "payload_open", "OPEN"), ("close", "payload_close", "CLOSE"), ("stop", "payload_stop", "STOP") })
                {
                    // An explicit null disables the command.
                    if (config.TryGetProperty(key, out var explicitValue) && explicitValue.ValueKind == JsonValueKind.Null)
                    {
                        continue;
                    }

                    var payload = config.Text(key) ?? fallback;
                    Commands[(endpoint.Key, CapabilityIds.Cover, command)] = _ => new CommandPlan([new MqttPublish(commandTopic, payload, Retained(config))], []);
                }
            }

            if (config.Str("set_position_topic") is { Length: > 0 } setPositionTopic
                && CommandTemplateOf(entity, "set_position_template", out var setPositionTemplate, "position")
                && open != closed)
            {
                Commands[(endpoint.Key, CapabilityIds.Cover, "setPosition")] = c =>
                    setPositionTemplate.Render(Conversions.Format(Math.Round(closed + (c.Number("position") * (open - closed) / 100)))) is { } payload
                        ? new CommandPlan([new MqttPublish(setPositionTopic, payload, Retained(config))], [])
                        : null;
            }
        }

        private void MapLock(HaEntity entity)
        {
            var config = entity.Config;
            var endpoint = Place(entity, CapabilityIds.Lock);
            endpoint.Add(CapabilityIds.Lock);
            SetDeviceClass(DeviceClasses.Lock, 10);
            var stateTopic = config.Str("state_topic");
            var optimistic = config.Bool("optimistic") ?? stateTopic is null;
            if (stateTopic is not null && Template(entity, "value_template", out var template))
            {
                var states = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [config.Text("state_locked") ?? "LOCKED"] = "locked",
                    [config.Text("state_unlocked") ?? "UNLOCKED"] = "unlocked",
                    [config.Text("state_jammed") ?? "JAMMED"] = "jammed",
                };
                Read(stateTopic, payload => template.Evaluate(payload)?.Text is { } text && states.TryGetValue(text, out var state)
                    ? [new MqttReading.State(endpoint.Key, CapabilityIds.Lock, "lockState", StateValue.String(state))]
                    : []);
            }

            if (config.Str("command_topic") is not { Length: > 0 } commandTopic || !CommandTemplateOf(entity, "command_template", out var command))
            {
                return;
            }

            foreach (var (name, key, fallback, state) in new[] { ("lock", "payload_lock", "LOCK", "locked"), ("unlock", "payload_unlock", "UNLOCK", "unlocked") })
            {
                var payload = config.Text(key) ?? fallback;
                Commands[(endpoint.Key, CapabilityIds.Lock, name)] = _ => command.Render(payload) is { } rendered
                    ? new CommandPlan(
                        [new MqttPublish(commandTopic, rendered, Retained(config))],
                        optimistic ? [new MqttReading.State(endpoint.Key, CapabilityIds.Lock, "lockState", StateValue.String(state))] : [])
                    : null;
            }
        }

        private void MapClimate(HaEntity entity)
        {
            var config = entity.Config;
            var endpoint = Place(entity, CapabilityIds.Thermostat);
            endpoint.Add(CapabilityIds.Thermostat);
            SetDeviceClass(DeviceClasses.Thermostat, 30);
            var fahrenheit = config.Str("temperature_unit") is "F" or "°F";
            Func<double, double> toCelsius = fahrenheit ? v => Math.Round((v - 32) * 5 / 9, 2) : v => v;
            Func<double, double> fromCelsius = fahrenheit ? v => Math.Round((v * 9 / 5) + 32, 1) : v => v;

            // Modes: Home Assistant names → catalogue names.
            var vendorModes = config.Get("modes") is { ValueKind: JsonValueKind.Array } list
                ? list.EnumerateArray().Select(Json.AsText).OfType<string>().ToList()
                : ["auto", "off", "cool", "heat", "dry", "fan_only"];
            var modes = vendorModes.Select(v => (Vendor: v, Mode: ModeOf(v))).Where(m => m.Mode is not null).ToList();
            if (modes.Count > 0)
            {
                endpoint.Constrain(CapabilityIds.Thermostat, "mode", new AttributeConstraint(Values: [.. _modeOrder.Where(m => modes.Any(x => x.Mode == m))]));
            }

            var min = Math.Max(5, config.Num("min_temp") is { } minTemp ? toCelsius(minTemp) : 7);
            var max = Math.Min(35, config.Num("max_temp") is { } maxTemp ? toCelsius(maxTemp) : 35);
            var step = config.Num("temp_step") is { } s && s >= 0.5 && Math.Abs((s / 0.5) - Math.Round(s / 0.5)) < 1e-9 && !fahrenheit ? s : (double?)null;
            var (low, high) = (Math.Ceiling(min * 2) / 2, Math.Floor(max * 2) / 2);
            if (low <= high)
            {
                endpoint.Constrain(CapabilityIds.Thermostat, "targetTemperature", new AttributeConstraint(Min: low, Max: high, Step: step));
            }

            var fans = config.Get("fan_modes") is { ValueKind: JsonValueKind.Array } fanList
                ? fanList.EnumerateArray().Select(Json.AsText).OfType<string>().Where(f => _fanOrder.Contains(f, StringComparer.Ordinal)).ToList()
                : ["auto", "low", "medium", "high"];
            if (config.Str("fan_mode_command_topic") is not null || config.Str("fan_mode_state_topic") is not null)
            {
                if (fans.Count > 0)
                {
                    endpoint.Constrain(CapabilityIds.Thermostat, "fanMode", new AttributeConstraint(Values: [.. _fanOrder.Where(fans.Contains)]));
                }
            }

            StateReader("mode_state_topic", "mode_state_template", value => ModeOf(value.Text) is { } mode ? StateValue.String(mode) : null, "mode");
            StateReader("temperature_state_topic", "temperature_state_template", value => value.Number is { } t ? StateValue.Number(toCelsius(t)) : null, "targetTemperature");
            StateReader("current_temperature_topic", "current_temperature_template", value => value.Number is { } t ? StateValue.Number(toCelsius(t)) : null, "currentTemperature");
            StateReader("fan_mode_state_topic", "fan_mode_state_template", value => fans.Contains(value.Text) ? StateValue.String(value.Text) : null, "fanMode");

            CommandWriter("mode_command_topic", "mode_command_template", "setMode", "mode", c =>
                modes.FirstOrDefault(m => m.Mode == c.Text("mode")).Vendor);
            CommandWriter("temperature_command_topic", "temperature_command_template", "setTargetTemperature", "targetTemperature", c =>
                Conversions.Format(fromCelsius(c.Number("targetTemperature"))));
            CommandWriter("fan_mode_command_topic", "fan_mode_command_template", "setFanMode", "fanMode", c => c.Text("fanMode"));

            void StateReader(string topicKey, string templateKey, Func<TemplateValue, StateValue?> convert, string attribute)
            {
                if (config.Str(topicKey) is { Length: > 0 } topic && Template(entity, templateKey, out var template))
                {
                    Read(topic, payload => template.Evaluate(payload) is { } value && convert(value) is { } state
                        ? [new MqttReading.State(endpoint.Key, CapabilityIds.Thermostat, attribute, state)]
                        : []);
                }
            }

            void CommandWriter(string topicKey, string templateKey, string command, string argument, Func<ValidatedCommand, string?> value)
            {
                if (config.Str(topicKey) is not { Length: > 0 } topic || !CommandTemplateOf(entity, templateKey, out var template))
                {
                    return;
                }

                var optimistic = config.Bool("optimistic") ?? config.Str(topicKey.Replace("_command_topic", "_state_topic", StringComparison.Ordinal)) is null;
                Commands[(endpoint.Key, CapabilityIds.Thermostat, command)] = c => value(c) is { } text && template.Render(text) is { } payload
                    ? new CommandPlan(
                        [new MqttPublish(topic, payload, Retained(config))],
                        optimistic ? [new MqttReading.State(endpoint.Key, CapabilityIds.Thermostat, argument, StateValue.FromJson(c.Argument(argument)))] : [])
                    : null;
            }
        }

        private static string? ModeOf(string? vendor) => vendor switch
        {
            "off" or "heat" or "cool" or "auto" or "dry" => vendor,
            "fan_only" => "fan",
            "heat_cool" => "auto",
            _ => null,
        };

        private void MapButton(HaEntity entity)
        {
            var config = entity.Config;
            if (config.Str("command_topic") is not { Length: > 0 } commandTopic || !CommandTemplateOf(entity, "command_template", out var template))
            {
                return;
            }

            var payload = config.Text("payload_press") ?? "PRESS";
            var name = Builder.Endpoint(DeviceBuilder.Main).AddGenericCommand("press" + Naming.PascalCase(entity.Name ?? entity.ObjectId), [], ConfirmationMode.AckIsFinal);
            Commands[(DeviceBuilder.Main, CapabilityIds.Generic, name)] = _ => template.Render(payload) is { } rendered
                ? new CommandPlan([new MqttPublish(commandTopic, rendered, Retained(config))], [], Final: true)
                : null;
        }

        // ---- helpers -----------------------------------------------------------------------------------------------

        private static void ConstrainColorTemperature(EndpointBuilder endpoint, JsonElement config, bool kelvin)
        {
            double? minK = config.Num("min_kelvin") ?? (config.Num("max_mireds") is { } maxMired && maxMired > 0 ? Math.Ceiling(1_000_000 / maxMired) : null);
            double? maxK = config.Num("max_kelvin") ?? (config.Num("min_mireds") is { } minMired && minMired > 0 ? Math.Floor(1_000_000 / minMired) : null);
            if (!kelvin && minK is null && maxK is null)
            {
                return;
            }

            var low = Math.Max(1000, Math.Ceiling(minK ?? 1000));
            var high = Math.Min(10_000, Math.Floor(maxK ?? 10_000));
            if (low <= high)
            {
                endpoint.Constrain(CapabilityIds.Color, "colorTemperature", new AttributeConstraint(Min: low, Max: high));
            }
        }

        private static IEnumerable<MqttReading> HueSaturation(string endpoint, (double Hue, double Saturation) value) =>
        [
            new MqttReading.State(endpoint, CapabilityIds.Color, "hue", StateValue.Number(value.Hue)),
            new MqttReading.State(endpoint, CapabilityIds.Color, "saturation", StateValue.Number(value.Saturation)),
        ];

        private static (double A, double B)? Pair(string text)
        {
            var parts = text.Split(',');
            return parts.Length == 2 && Conversions.Number(parts[0]) is { } a && Conversions.Number(parts[1]) is { } b ? (a, b) : null;
        }

        private static (double Hue, double Saturation) RgbToHs(double r, double g, double b)
        {
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            if (max <= 0)
            {
                return (0, 0);
            }

            var delta = max - min;
            double hue = delta == 0 ? 0
                : max == r ? 60 * (((g - b) / delta) % 6)
                : max == g ? 60 * (((b - r) / delta) + 2)
                : 60 * (((r - g) / delta) + 4);
            return (Math.Round(hue < 0 ? hue + 360 : hue, 1), Math.Round(delta / max * 100));
        }

        private static (int R, int G, int B) HsToRgb(double hue, double saturation)
        {
            var s = Math.Clamp(saturation, 0, 100) / 100;
            var h = ((hue % 360) + 360) % 360 / 60;
            var x = s * (1 - Math.Abs((h % 2) - 1));
            var (r, g, b) = (int)h switch
            {
                0 => (s, x, 0d),
                1 => (x, s, 0d),
                2 => (0d, s, x),
                3 => (0d, x, s),
                4 => (x, 0d, s),
                _ => (s, 0d, x),
            };
            var m = 1 - s;
            return ((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
        }

        /// <summary>A rendered value against on/off payloads; a JSON boolean counts directly.</summary>
        private static bool? OnOff(TemplateValue value, string on, string off) =>
            value.Text == on ? true
            : value.Text == off ? false
            : value.Json is { ValueKind: JsonValueKind.True } ? true
            : value.Json is { ValueKind: JsonValueKind.False } ? false
            : null;

        private static bool Retained(JsonElement config) => config.Bool("retain") == true;

        private static string EntityKey(HaEntity entity) => Naming.CamelCase(entity.Name ?? entity.ObjectId);

        private EndpointBuilder Place(HaEntity entity, string capability) =>
            Builder.Place(capability, DeviceBuilder.Main, Naming.EndpointKey(entity.ObjectId), entity.Name);

        /// <summary>The endpoint for one attribute: <c>main</c> while it is unbound there, else one named after the entity.</summary>
        private EndpointBuilder? Free(HaEntity entity, string capability, string attribute)
        {
            if (Bound.Add((DeviceBuilder.Main, capability, attribute)))
            {
                return Builder.Endpoint(DeviceBuilder.Main);
            }

            var key = Naming.EndpointKey(entity.ObjectId);
            return Bound.Add((key, capability, attribute)) ? Builder.Endpoint(key, entity.Name) : null;
        }

        private void SetDeviceClass(string deviceClass, int rank)
        {
            if (rank < _deviceClassRank)
            {
                (_deviceClass, _deviceClassRank) = (deviceClass, rank);
            }
        }

        private void Read(string topic, Func<string, IEnumerable<MqttReading>> reader)
        {
            if (!Readers.TryGetValue(topic, out var list))
            {
                list = [];
                Readers[topic] = list;
            }

            list.Add(reader);
        }

        private bool Template(HaEntity entity, string key, out ValueTemplate template)
        {
            var text = entity.Config.Str(key);
            if (ValueTemplate.TryParse(text, out template))
            {
                return true;
            }

            Unsupported.Add(($"{entity.Key}.{key}", text!));
            return false;
        }

        private bool CommandTemplateOf(HaEntity entity, string key, out CommandTemplate template, string variable = "value")
        {
            var text = entity.Config.Str(key);
            if (CommandTemplate.TryParse(text, out template, variable))
            {
                return true;
            }

            Unsupported.Add(($"{entity.Key}.{key}", text!));
            return false;
        }

        /// <summary>A sensor whose template is unsupported: its raw payload in a diagnostic text attribute.</summary>
        private void Raw(HaEntity entity, string stateTopic)
        {
            var key = Builder.Endpoint(DeviceBuilder.Main).AddGeneric(EntityKey(entity), new StringSchema(255), category: AttributeCategory.Diagnostic);
            Read(stateTopic, payload => payload.Length <= 255
                ? [new MqttReading.State(DeviceBuilder.Main, CapabilityIds.Generic, key, StateValue.String(payload))]
                : [new MqttReading.State(DeviceBuilder.Main, CapabilityIds.Generic, key, StateValue.String(payload[..255]))]);
        }
    }
}

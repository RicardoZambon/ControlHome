using System.Globalization;
using System.Text.Json;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.Mqtt.Model;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;

namespace ControlHome.Connectors.Mqtt.Tasmota;

/// <summary>
/// Maps Tasmota's native discovery (<c>tasmota/discovery/&lt;MAC&gt;/config</c> and <c>…/sensors</c>, both retained) to a
/// device: relays (<c>rl</c> 1 = relay, 2 = light; shutters are not mapped) → switch (+ level for dimmable lights), one
/// endpoint (<c>main</c>) for a single relay or <c>relay1…n</c> for several; <c>ENERGY</c> → power (W), energy (Total
/// kWh → Wh) and generic today/yesterday energy, voltage, current, apparent/reactive power and power factor on
/// <c>main</c>; other sensors → temperature, humidity, air quality and generic measurements, each sensor on <c>main</c>
/// while its attributes are free there, else on an endpoint named after the sensor.
/// </summary>
internal static class TasmotaMapper
{
    public const string ExternalIdPrefix = "tasmota:";

    /// <summary>The device for a config (and the latest sensors payload, if any); <c>null</c> when the config is unusable.</summary>
    public static TasmotaDevice? Map(JsonElement config, JsonElement? sensors)
    {
        if (config.ValueKind != JsonValueKind.Object || config.Str("mac") is not { Length: > 0 } mac || config.Str("t") is not { Length: > 0 } topic)
        {
            return null;
        }

        var prefixes = config.Array("tp").Select(Json.AsText).ToList();
        var topics = new TasmotaTopics(
            config.Str("ft") ?? "%prefix%/%topic%/",
            topic,
            config.Str("hn") ?? topic,
            mac,
            prefixes.ElementAtOrDefault(0) ?? "cmnd",
            prefixes.ElementAtOrDefault(1) ?? "stat",
            prefixes.ElementAtOrDefault(2) ?? "tele");
        var states = config.Array("state").Select(Json.AsText).ToList();
        var friendlyNames = config.Array("fn").Select(Json.AsText).ToList();
        var builder = new DeviceBuilder();

        // Relays: 1 = relay, 2 = light.
        var relayTypes = config.Array("rl").Select(r => r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 0).ToList();
        var relayIndexes = relayTypes.Select((type, i) => (Type: type, Index: i + 1)).Where(r => r.Type is 1 or 2).ToList();
        var relays = new List<(int Index, string Endpoint)>();
        string? dimmer = null;
        var dimmable = (config.Int("lt_st") ?? 0) > 0;
        foreach (var (type, index) in relayIndexes)
        {
            var key = relayIndexes.Count == 1 ? DeviceBuilder.Main : string.Create(CultureInfo.InvariantCulture, $"relay{index}");
            var name = relayIndexes.Count == 1 ? null : friendlyNames.ElementAtOrDefault(index - 1);
            var endpoint = builder.Endpoint(key, name).Add(CapabilityIds.Switch);
            relays.Add((index, key));
            if (type == 2 && dimmable && dimmer is null)
            {
                endpoint.Add(CapabilityIds.Level);
                dimmer = key;
            }
        }

        var sensorBindings = new List<TasmotaSensorBinding>();
        var hasEnergy = false;
        if (sensors?.Get("sn") is { ValueKind: JsonValueKind.Object } sn)
        {
            hasEnergy = MapSensors(sn, builder, sensorBindings);
        }

        var light = relayIndexes.Any(r => r.Type == 2);
        var deviceClass = light ? DeviceClasses.Light
            : relays.Count > 0 ? hasEnergy ? DeviceClasses.Outlet : DeviceClasses.Switch
            : sensorBindings.Count > 0 ? DeviceClasses.Sensor
            : null;
        var discovered = new DiscoveredDevice(
            ExternalIdPrefix + mac.ToUpperInvariant(),
            config.Str("dn") ?? friendlyNames.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? config.Str("hn") ?? topic,
            deviceClass,
            "Tasmota",
            config.Str("md"),
            config.Str("sw"),
            null,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["mac"] = FormatMac(mac) },
            null,
            builder.Build());
        return new TasmotaDevice(
            discovered,
            topics,
            relays,
            dimmer,
            sensorBindings,
            (states.ElementAtOrDefault(0) ?? "OFF", states.ElementAtOrDefault(1) ?? "ON"),
            (config.Str("onln") ?? "Online", config.Str("ofln") ?? "Offline"));
    }

    /// <summary>Maps the <c>sn</c> object of the sensors message; returns whether it has energy metering.</summary>
    private static bool MapSensors(JsonElement sn, DeviceBuilder builder, List<TasmotaSensorBinding> bindings)
    {
        var tempUnit = sn.Str("TempUnit");
        var toCelsius = Conversions.ToCanonical("temperature", tempUnit) ?? (v => v);
        var pressureUnit = sn.Str("PressureUnit") ?? Conversions.HectoPascal;
        var bound = new HashSet<(string Endpoint, string Capability, string Attribute)>();
        var hasEnergy = false;
        foreach (var sensor in sn.EnumerateObject())
        {
            if (sensor.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (sensor.Name == "ENERGY")
            {
                hasEnergy = true;
                var main = builder.Endpoint(DeviceBuilder.Main);
                foreach (var property in sensor.Value.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Number))
                {
                    switch (property.Name)
                    {
                        case "Power":
                            Catalogue(main, property.Name, CapabilityIds.Power, "power", v => v);
                            break;
                        case "Total":
                            Catalogue(main, property.Name, CapabilityIds.Energy, "energy", v => Math.Round(v * 1000, 3));
                            break;
                        case "Today":
                            Generic(main, property.Name, "energyToday", Units.WattHour, StateClass.TotalIncreasing, AttributeCategory.Primary, v => Math.Round(v * 1000, 3));
                            break;
                        case "Yesterday":
                            Generic(main, property.Name, "energyYesterday", Units.WattHour, StateClass.None, AttributeCategory.Diagnostic, v => Math.Round(v * 1000, 3));
                            break;
                        case "Voltage":
                            Generic(main, property.Name, "voltage", Conversions.Volt, StateClass.Measurement, AttributeCategory.Primary, v => v);
                            break;
                        case "Current":
                            Generic(main, property.Name, "current", Conversions.Ampere, StateClass.Measurement, AttributeCategory.Primary, v => v);
                            break;
                        case "ApparentPower":
                            Generic(main, property.Name, "apparentPower", "VA", StateClass.Measurement, AttributeCategory.Diagnostic, v => v);
                            break;
                        case "ReactivePower":
                            Generic(main, property.Name, "reactivePower", "var", StateClass.Measurement, AttributeCategory.Diagnostic, v => v);
                            break;
                        case "Factor":
                            Generic(main, property.Name, "powerFactor", null, StateClass.Measurement, AttributeCategory.Diagnostic, v => v);
                            break;
                    }
                }

                continue;
            }

            // Another sensor (AM2301, DS18B20-1, BME280, …): main while its catalogue attributes are free there.
            var catalogue = sensor.Value.EnumerateObject()
                .Select(p => Catalogued(p.Name))
                .OfType<(string Capability, string Attribute)>()
                .ToList();
            var endpointKey = catalogue.Any(c => bound.Contains((DeviceBuilder.Main, c.Capability, c.Attribute)))
                ? Naming.EndpointKey(sensor.Name)
                : DeviceBuilder.Main;
            var endpoint = builder.Endpoint(endpointKey, endpointKey == DeviceBuilder.Main ? null : sensor.Name);
            foreach (var property in sensor.Value.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Number))
            {
                switch (property.Name)
                {
                    case "Temperature":
                        Catalogue(endpoint, property.Name, CapabilityIds.Temperature, "temperature", v => Math.Round(toCelsius(v), 2));
                        break;
                    case "Humidity":
                        Catalogue(endpoint, property.Name, CapabilityIds.Humidity, "humidity", v => Math.Clamp(v, 0, 100));
                        break;
                    case "CarbonDioxide" or "eCO2":
                        Catalogue(endpoint, property.Name, CapabilityIds.AirQuality, "co2", v => v);
                        break;
                    case "PM2.5":
                        Catalogue(endpoint, property.Name, CapabilityIds.AirQuality, "pm25", v => v);
                        break;
                    case "TVOC":
                        Catalogue(endpoint, property.Name, CapabilityIds.AirQuality, "voc", v => v);
                        break;
                    case "DewPoint":
                        Generic(endpoint, property.Name, "dewPoint", Units.Celsius, StateClass.Measurement, AttributeCategory.Primary, v => Math.Round(toCelsius(v), 2));
                        break;
                    case "Pressure" or "SeaPressure":
                        Generic(endpoint, property.Name, Naming.CamelCase(property.Name), pressureUnit, StateClass.Measurement, AttributeCategory.Primary, v => v);
                        break;
                    case "Illuminance":
                        Generic(endpoint, property.Name, "illuminance", Conversions.Lux, StateClass.Measurement, AttributeCategory.Primary, v => v);
                        break;
                    default:
                        Generic(endpoint, property.Name, Naming.CamelCase(property.Name), null, StateClass.Measurement, AttributeCategory.Primary, v => v);
                        break;
                }
            }

            void Catalogue(EndpointBuilder target, string property, string capability, string attribute, Func<double, double> convert)
            {
                if (!bound.Add((target.Key, capability, attribute)))
                {
                    Generic(target, property, Naming.CamelCase(property), null, StateClass.Measurement, AttributeCategory.Primary, convert);
                    return;
                }

                target.Add(capability);
                bindings.Add(new TasmotaSensorBinding(sensor.Name, property, target.Key, capability, attribute, convert));
            }

            void Generic(EndpointBuilder target, string property, string key, string? unit, StateClass stateClass, AttributeCategory category, Func<double, double> convert)
            {
                var attribute = target.AddGeneric(key, new NumberSchema(Unit: unit), stateClass, category, recorded: stateClass != StateClass.None);
                bindings.Add(new TasmotaSensorBinding(sensor.Name, property, target.Key, CapabilityIds.Generic, attribute, convert));
            }
        }

        return hasEnergy;

        static (string Capability, string Attribute)? Catalogued(string property) => property switch
        {
            "Temperature" => (CapabilityIds.Temperature, "temperature"),
            "Humidity" => (CapabilityIds.Humidity, "humidity"),
            "CarbonDioxide" or "eCO2" => (CapabilityIds.AirQuality, "co2"),
            "PM2.5" => (CapabilityIds.AirQuality, "pm25"),
            "TVOC" => (CapabilityIds.AirQuality, "voc"),
            _ => null,
        };
    }

    /// <summary>"AABBCCDDEEFF" → "AA:BB:CC:DD:EE:FF".</summary>
    private static string FormatMac(string mac)
    {
        var hex = mac.Replace(":", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        return hex.Length == 12 ? string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2))) : hex;
    }
}

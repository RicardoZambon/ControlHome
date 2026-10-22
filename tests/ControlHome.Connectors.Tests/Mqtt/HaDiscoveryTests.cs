using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.Mqtt.HomeAssistant;
using ControlHome.Connectors.Mqtt.Model;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;

namespace ControlHome.Connectors.Tests.Mqtt;

/// <summary>Home Assistant discovery: topic parsing, abbreviations, <c>~</c>, and the mapping to devices (fixtures).</summary>
public sealed class HaDiscoveryTests
{
    private const string Prefix = "homeassistant";

    private static readonly IReadOnlyDictionary<string, string> _configs = MqttFixtures.HaConfigs();

    [Theory]
    [InlineData("homeassistant/sensor/temp1/config", "sensor", null, "temp1")]
    [InlineData("homeassistant/binary_sensor/garden/motion/config", "binary_sensor", "garden", "motion")]
    [InlineData("homeassistant/device/kitchen-climate/config", "device", null, "kitchen-climate")]
    public void Config_topics_are_recognised(string topic, string component, string? node, string objectId)
    {
        HaDiscoveryParser.ParseTopic(Prefix, topic).ShouldBe(new HaConfigTopic(topic, component, node, objectId));
    }

    [Theory]
    [InlineData("homeassistant/status")]
    [InlineData("homeassistant/binary_sensor/garden/state")]
    [InlineData("homeassistant/a/b/c/d/config")]
    [InlineData("homeassistant/device/node/x/config")]
    [InlineData("other/sensor/temp1/config")]
    public void Other_topics_are_not_configs(string topic)
    {
        HaDiscoveryParser.ParseTopic(Prefix, topic).ShouldBeNull();
    }

    [Fact]
    public void Device_based_configs_expand_abbreviations_base_topics_and_shared_options()
    {
        var message = Parse("homeassistant/device/kitchen-climate/config");

        message.DeviceKey.ShouldBe("ha:ea334450945afc");
        message.FromZigbee2Mqtt.ShouldBeFalse();
        message.Device!.Value.Str("manufacturer").ShouldBe("Bla electronics");
        message.Device!.Value.Str("suggested_area").ShouldBe("Kitchen");
        message.Entities.Select(e => (e.Component, e.Key)).ShouldBe(
        [
            ("sensor", "temp01ae_t"), ("sensor", "temp01ae_h"), ("sensor", "temp01ae_b"), ("sensor", "temp01ae_lux"),
            ("sensor", "temp01ae_dew"), ("update", "temp01ae_fw"),
        ]);
        var temperature = message.Entities[0].Config;
        temperature.Str("state_topic").ShouldBe("kitchen/climate/state");
        temperature.Str("availability_topic").ShouldBe("kitchen/climate/availability");
        temperature.Str("device_class").ShouldBe("temperature");
        temperature.Str("unit_of_measurement").ShouldBe("°C");
        temperature.Str("value_template").ShouldBe("{{ value_json.temperature }}");
        temperature.Num("qos").ShouldBe(2);
    }

    [Fact]
    public void A_device_based_component_with_only_its_platform_is_being_removed()
    {
        var topic = HaDiscoveryParser.ParseTopic(Prefix, "homeassistant/device/d1/config")!;

        var message = HaDiscoveryParser.Parse(topic, """
            {"dev":{"ids":"d1"},"o":{"name":"x"},"cmps":{"a":{"p":"sensor","stat_t":"d1/a"},"b":{"p":"sensor"}}}
            """)!;

        message.Entities.Select(e => e.Key).ShouldBe(["a"]);
    }

    [Fact]
    public void Base_topics_expand_at_either_end_and_inside_availability_lists()
    {
        var topic = HaDiscoveryParser.ParseTopic(Prefix, "homeassistant/switch/x/config")!;

        var entity = HaDiscoveryParser.Parse(topic, """
            {"~":"home/x","cmd_t":"~/set","stat_t":"state/~","avty":[{"t":"~/online","pl_avail":"yes"}],"name":"~ is not a topic"}
            """)!.Entities.Single();

        entity.Config.Str("command_topic").ShouldBe("home/x/set");
        entity.Config.Str("state_topic").ShouldBe("state/home/x");
        entity.Config.Str("name").ShouldBe("~ is not a topic");
        var availability = entity.Config.Array("availability").Single();
        availability.Str("topic").ShouldBe("home/x/online");
        availability.Str("payload_available").ShouldBe("yes");
        entity.Key.ShouldBe("x");
    }

    [Fact]
    public void Devices_are_keyed_by_identifiers_then_connections_then_the_entity()
    {
        HaDiscoveryParser.DeviceKey(MqttFixtures.Parse("""{"identifiers":["a","b"]}"""), "f").ShouldBe("ha:a");
        HaDiscoveryParser.DeviceKey(MqttFixtures.Parse("""{"identifiers":"a"}"""), "f").ShouldBe("ha:a");
        HaDiscoveryParser.DeviceKey(MqttFixtures.Parse("""{"connections":[["mac","02:00:00:00:00:01"]]}"""), "f").ShouldBe("ha:mac_02:00:00:00:00:01");
        HaDiscoveryParser.DeviceKey(null, "sensor_x").ShouldBe("ha:sensor_x");
    }

    [Fact]
    public void Configs_published_by_zigbee2mqtt_are_recognised()
    {
        Parse("homeassistant/sensor/0x00158d0000000003/temperature/config").FromZigbee2Mqtt.ShouldBeTrue();
    }

    [Fact]
    public void A_device_based_sensor_device_maps_its_entities_and_marks_unsupported_templates()
    {
        var message = Parse("homeassistant/device/kitchen-climate/config");
        var device = HaDeviceMapper.Map(message.DeviceKey, message.Device, message.Entities);
        var discovered = device.Discovered;

        discovered.ExternalId.ShouldBe("ha:ea334450945afc");
        discovered.Name.ShouldBe("Kitchen climate");
        discovered.Manufacturer.ShouldBe("Bla electronics");
        discovered.Model.ShouldBe("xya");
        discovered.Firmware.ShouldBe("1.0");
        discovered.SuggestedRoom.ShouldBe("Kitchen");
        discovered.Connections["serial"].ShouldBe("ea334450945afc");
        discovered.DeviceClass.ShouldBe(DeviceClasses.Sensor);
        discovered.Capabilities().ShouldBe([CapabilityIds.Temperature, CapabilityIds.Humidity, CapabilityIds.Battery, CapabilityIds.Generic]);
        var generic = discovered.Generic();
        generic.Attributes.Select(a => (a.Key, a.Category)).ShouldBe(
        [
            ("lightLevel", AttributeCategory.Primary),
            ("dewPoint", AttributeCategory.Diagnostic),
            (HaDeviceMapper.UnsupportedAttribute, AttributeCategory.Diagnostic),
        ]);
        generic.FindAttribute("lightLevel")!.Unit.ShouldBe("lx");
        device.HasAvailabilityTopic.ShouldBeTrue();
        device.Topics.ShouldBe(["kitchen/climate/availability", "kitchen/climate/state"], ignoreOrder: true);

        var unsupported = device.InitialReadings.Value(CapabilityIds.Generic, HaDeviceMapper.UnsupportedAttribute)!;
        unsupported.J!.Value.GetProperty("temp01ae_dew.value_template").GetString().ShouldBe("{{ (value_json.dew * 10) | round }}");

        var readings = device.Read("kitchen/climate/state", """{"temperature":21.5,"humidity":"45.25","battery":87.6,"lux":120,"dew":12.3}""");
        readings.Value(CapabilityIds.Temperature, "temperature").ShouldBe(StateValue.Number(21.5));
        readings.Value(CapabilityIds.Humidity, "humidity").ShouldBe(StateValue.Number(45.2));
        readings.Value(CapabilityIds.Battery, "battery").ShouldBe(StateValue.Number(87));
        readings.Value(CapabilityIds.Generic, "lightLevel").ShouldBe(StateValue.Number(120));
        readings.Value(CapabilityIds.Generic, "dewPoint")!.S!.ShouldContain("\"dew\":12.3"); // the raw payload, kept for diagnosis
        discovered.ShouldAllFit(readings);
        discovered.ShouldAllFit(device.InitialReadings);
        device.Read("kitchen/climate/availability", "online").ShouldBe([new MqttReading.Reachability(Availability.Online)]);
        device.Read("kitchen/climate/availability", "offline").ShouldBe([new MqttReading.Reachability(Availability.Offline)]);
    }

    [Fact]
    public void A_motion_sensor_without_availability_counts_as_online_when_it_reports()
    {
        var device = Map("homeassistant/binary_sensor/garden/motion/config");

        device.Discovered.ExternalId.ShouldBe("ha:01ad");
        device.Discovered.Name.ShouldBe("Garden");
        device.Discovered.Capabilities().ShouldBe([CapabilityIds.Motion]);
        device.HasAvailabilityTopic.ShouldBeFalse();
        device.Read("homeassistant/binary_sensor/garden/state", "ON").Value(CapabilityIds.Motion, "motion").ShouldBe(StateValue.True);
        device.Read("homeassistant/binary_sensor/garden/state", "OFF").Value(CapabilityIds.Motion, "motion").ShouldBe(StateValue.False);
        device.Read("homeassistant/binary_sensor/garden/state", "maybe").ShouldBeEmpty();
    }

    [Fact]
    public void A_default_schema_light_maps_brightness_color_temperature_and_hs()
    {
        var light = Map("homeassistant/light/office_lamp/config");
        var device = light.Discovered;

        device.ExternalId.ShouldBe("ha:office-lamp-1");
        device.DeviceClass.ShouldBe(DeviceClasses.Light);
        device.Capabilities().ShouldBe([CapabilityIds.Switch, CapabilityIds.Level, CapabilityIds.Color]);
        device.Constraint(CapabilityIds.Color, "colorTemperature").ShouldBe(new AttributeConstraint(Min: 2703, Max: 6535));

        light.Read("home/office/lamp/state", "on").Value(CapabilityIds.Switch, "on").ShouldBe(StateValue.True);
        light.Read("home/office/lamp/brightness", "40").Value(CapabilityIds.Level, "level").ShouldBe(StateValue.Number(40));
        light.Read("home/office/lamp/ct", "250").Value(CapabilityIds.Color, "colorTemperature").ShouldBe(StateValue.Number(4000));
        var hs = light.Read("home/office/lamp/hs", "30.5,80");
        hs.Value(CapabilityIds.Color, "hue").ShouldBe(StateValue.Number(30.5));
        hs.Value(CapabilityIds.Color, "saturation").ShouldBe(StateValue.Number(80));

        light.Publishes(CapabilityIds.Switch, "turnOn").ShouldBe([new MqttPublish("home/office/lamp/set", "on")]);
        light.Publishes(CapabilityIds.Level, "setLevel", """{"level":40}""").ShouldBe([new MqttPublish("home/office/lamp/brightness/set", "40")]);
        light.Publishes(CapabilityIds.Color, "setColorTemperature", """{"colorTemperature":4000}""").ShouldBe([new MqttPublish("home/office/lamp/ct/set", "250")]);
        light.Publishes(CapabilityIds.Color, "setColor", """{"hue":120,"saturation":50}""").ShouldBe([new MqttPublish("home/office/lamp/hs/set", "120,50")]);
    }

    [Fact]
    public void A_json_schema_rgb_light_and_its_button_form_one_device()
    {
        var messages = new[] { Parse("homeassistant/light/esp-strip/strip/config"), Parse("homeassistant/button/esp-strip/restart/config") };
        messages.Select(m => m.DeviceKey).Distinct().ShouldBe(["ha:esp-strip"]);
        var strip = HaDeviceMapper.Map("ha:esp-strip", messages[0].Device, [.. messages.SelectMany(m => m.Entities)]);
        var device = strip.Discovered;

        device.Name.ShouldBe("ESP strip");
        device.Firmware.ShouldBe("esphome v2026.9.0");
        device.Connections["mac"].ShouldBe("24:0a:c4:00:00:01");
        device.Capabilities().ShouldBe([CapabilityIds.Switch, CapabilityIds.Level, CapabilityIds.Color, CapabilityIds.Generic]);
        device.Generic().Commands.Select(c => c.Name).ShouldBe(["pressRestart"]);

        var readings = strip.Read("esp-strip/light/strip/state", """{"state":"ON","brightness":128,"color_mode":"rgb","color":{"r":255,"g":0,"b":0}}""");
        readings.Value(CapabilityIds.Switch, "on").ShouldBe(StateValue.True);
        readings.Value(CapabilityIds.Level, "level").ShouldBe(StateValue.Number(50));
        readings.Value(CapabilityIds.Color, "hue").ShouldBe(StateValue.Number(0));
        readings.Value(CapabilityIds.Color, "saturation").ShouldBe(StateValue.Number(100));
        device.ShouldAllFit(readings);

        strip.Publishes(CapabilityIds.Color, "setColor", """{"hue":120,"saturation":100}""").ShouldBe(
            [new MqttPublish("esp-strip/light/strip/command", """{"state":"ON","color":{"r":0,"g":255,"b":0}}""")]);
        strip.Publishes(CapabilityIds.Level, "setLevel", """{"level":100}""").ShouldBe(
            [new MqttPublish("esp-strip/light/strip/command", """{"state":"ON","brightness":255}""")]);
        var (plan, _, _) = strip.Plan(new DeviceCommandBatch("ha:esp-strip", [new("main", CapabilityIds.Generic, "pressRestart", MqttFixtures.Parse("{}"))], "k"));
        plan!.Publishes.ShouldBe([new MqttPublish("esp-strip/button/restart/command", "PRESS")]);
        plan.CompletesOnPublish.ShouldBeTrue();
    }

    [Fact]
    public void A_switch_uses_its_payloads_state_values_and_template()
    {
        var porch = Map("homeassistant/switch/porch/config");

        porch.Discovered.DeviceClass.ShouldBe(DeviceClasses.Outlet);
        porch.Read("porch/relay/state", """{"relay":"on"}""").Value(CapabilityIds.Switch, "on").ShouldBe(StateValue.True);
        porch.Read("porch/relay/state", """{"relay":"off"}""").Value(CapabilityIds.Switch, "on").ShouldBe(StateValue.False);
        porch.Publishes(CapabilityIds.Switch, "turnOn").ShouldBe([new MqttPublish("porch/relay/set", "1")]);
        porch.Publishes(CapabilityIds.Switch, "toggle").ShouldBe([new MqttPublish("porch/relay/set", "1")]); // last state: off
    }

    [Fact]
    public void A_garage_cover_scales_positions_and_is_safety_critical_to_open()
    {
        var garage = Map("homeassistant/cover/garage/config");

        garage.Discovered.DeviceClass.ShouldBe(DeviceClasses.Garage);
        garage.Read("garage/door/position", "255").Value(CapabilityIds.Cover, "position").ShouldBe(StateValue.Number(100));
        garage.Read("garage/door/state", "closing").Value(CapabilityIds.Cover, "coverState").ShouldBe(StateValue.String("closing"));
        garage.Publishes(CapabilityIds.Cover, "open").ShouldBe([new MqttPublish("garage/door/set", "OPEN")]);
        garage.Publishes(CapabilityIds.Cover, "setPosition", """{"position":50}""").ShouldBe([new MqttPublish("garage/door/position/set", """{"position": 128}""")]);
        garage.Rejection(CapabilityIds.Cover, "stop").ShouldNotBeNull(); // payload_stop: null disables it
        garage.Plan(new DeviceCommandBatch("ha:garage-1", [new("main", CapabilityIds.Cover, "open", MqttFixtures.Parse("{}"))], "k")).SafetyCritical.ShouldBeTrue();
    }

    [Fact]
    public void A_lock_reports_its_states_and_sends_its_payloads()
    {
        var doorLock = Map("homeassistant/lock/front/config");

        doorLock.Discovered.DeviceClass.ShouldBe(DeviceClasses.Lock);
        doorLock.Read("front/lock/state", "LOCKED").Value(CapabilityIds.Lock, "lockState").ShouldBe(StateValue.String("locked"));
        doorLock.Read("front/lock/state", "JAMMED").Value(CapabilityIds.Lock, "lockState").ShouldBe(StateValue.String("jammed"));
        doorLock.Read("front/lock/state", "LOCKING").ShouldBeEmpty();
        doorLock.Publishes(CapabilityIds.Lock, "unlock").ShouldBe([new MqttPublish("front/lock/set", "UNLOCK")]);
    }

    [Fact]
    public void A_climate_entity_maps_modes_targets_and_fans()
    {
        var ac = Map("homeassistant/climate/living_ac/config");
        var device = ac.Discovered;

        device.DeviceClass.ShouldBe(DeviceClasses.Thermostat);
        device.Constraint(CapabilityIds.Thermostat, "mode")!.Values.ShouldBe(["off", "cool", "heat", "auto", "fan"]);
        device.Constraint(CapabilityIds.Thermostat, "targetTemperature").ShouldBe(new AttributeConstraint(Min: 16, Max: 30, Step: 0.5));
        device.Constraint(CapabilityIds.Thermostat, "fanMode")!.Values.ShouldBe(["auto", "low", "high"]);
        ac.Read("living/ac/mode", "fan_only").Value(CapabilityIds.Thermostat, "mode").ShouldBe(StateValue.String("fan"));
        ac.Read("living/ac/mode", "heat_cool").Value(CapabilityIds.Thermostat, "mode").ShouldBe(StateValue.String("auto"));
        ac.Read("living/ac/target", "23.5").Value(CapabilityIds.Thermostat, "targetTemperature").ShouldBe(StateValue.Number(23.5));
        ac.Read("living/ac/current", """{"temperature":26.1}""").Value(CapabilityIds.Thermostat, "currentTemperature").ShouldBe(StateValue.Number(26.1));
        ac.Publishes(CapabilityIds.Thermostat, "setMode", """{"mode":"fan"}""").ShouldBe([new MqttPublish("living/ac/mode/set", "fan_only")]);
        ac.Publishes(CapabilityIds.Thermostat, "setMode", """{"mode":"auto"}""").ShouldBe([new MqttPublish("living/ac/mode/set", "heat_cool")]);
        ac.Publishes(CapabilityIds.Thermostat, "setTargetTemperature", """{"targetTemperature":22.5}""").ShouldBe([new MqttPublish("living/ac/target/set", "22.5")]);
        ac.Publishes(CapabilityIds.Thermostat, "setFanMode", """{"fanMode":"low"}""").ShouldBe([new MqttPublish("living/ac/fan/set", "low")]);
    }

    [Fact]
    public void A_switch_without_a_state_topic_is_optimistic()
    {
        var topic = HaDiscoveryParser.ParseTopic(Prefix, "homeassistant/switch/fan/config")!;
        var message = HaDiscoveryParser.Parse(topic, """{"name":"Fan","cmd_t":"fan/set","uniq_id":"fan-1"}""")!;
        var fan = HaDeviceMapper.Map(message.DeviceKey, message.Device, message.Entities);

        var (plan, _, _) = fan.Plan(new DeviceCommandBatch(fan.ExternalId, [new("main", CapabilityIds.Switch, "turnOn", MqttFixtures.Parse("{}"))], "k"));

        plan!.Optimistic.Value(CapabilityIds.Switch, "on").ShouldBe(StateValue.True);
        plan.CompletesOnPublish.ShouldBeTrue();
    }

    [Fact]
    public void An_unsupported_command_template_rejects_only_that_command()
    {
        var topic = HaDiscoveryParser.ParseTopic(Prefix, "homeassistant/switch/odd/config")!;
        var message = HaDiscoveryParser.Parse(topic, """
            {"name":"Odd","cmd_t":"odd/set","stat_t":"odd/state","cmd_tpl":"{{ 'on' if value == 'ON' else 'off' }}","uniq_id":"odd-1"}
            """)!;
        var odd = HaDeviceMapper.Map(message.DeviceKey, message.Device, message.Entities);

        odd.Read("odd/state", "ON").Value(CapabilityIds.Switch, "on").ShouldBe(StateValue.True);
        odd.Rejection(CapabilityIds.Switch, "turnOn")!.ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe("unsupported_command");
        odd.InitialReadings.Value(CapabilityIds.Generic, HaDeviceMapper.UnsupportedAttribute)!.J!.Value
            .TryGetProperty("odd-1.command_template", out _).ShouldBeTrue();
    }

    private static HaDiscoveryMessage Parse(string topic) =>
        HaDiscoveryParser.Parse(HaDiscoveryParser.ParseTopic(Prefix, topic)!, _configs[topic])!;

    private static HaDevice Map(string topic)
    {
        var message = Parse(topic);
        return HaDeviceMapper.Map(message.DeviceKey, message.Device, message.Entities);
    }
}

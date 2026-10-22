using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.Mqtt.Model;
using ControlHome.Connectors.Mqtt.Transport;
using ControlHome.Connectors.Mqtt.Zigbee2Mqtt;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;

namespace ControlHome.Connectors.Tests.Mqtt;

/// <summary>Pure mapping of Zigbee2MQTT's <c>bridge/devices</c> (fixture) to devices, readings and command payloads.</summary>
public sealed class Z2mDeviceMapperTests
{
    private const string Base = "zigbee2mqtt";
    private const string Bridge = "0x00124b0000c0ffee";

    private static readonly IReadOnlyDictionary<string, Z2mDevice> _devices = MqttFixtures.Json("z2m-bridge-devices.json")
        .EnumerateArray()
        .Select(e => Z2mDeviceMapper.Map(e, Base, Bridge))
        .OfType<Z2mDevice>()
        .ToDictionary(d => d.FriendlyName);

    [Fact]
    public void Maps_every_interviewed_enabled_device_but_not_the_coordinator()
    {
        _devices.Keys.ShouldBe(
        [
            "living_room/ceiling", "kitchen_plug", "bedroom_climate", "front_door", "hallway_motion", "laundry_leak",
            "front_door_lock", "office_blind", "bedroom_trv", "desk_remote", "hall_2gang", "led_strip", "garage_door",
        ],
            ignoreOrder: true);
        _devices.Values.ShouldAllBe(d => d.Discovered.ParentExternalId == Bridge && d.Discovered.ExternalId.StartsWith("0x"));
    }

    [Fact]
    public void A_color_light_gets_switch_level_color_and_generic_properties()
    {
        var light = _devices["living_room/ceiling"];
        var device = light.Discovered;

        device.ExternalId.ShouldBe("0x0017880100000001");
        device.Name.ShouldBe("living_room/ceiling");
        device.SuggestedRoom.ShouldBe("living room");
        device.DeviceClass.ShouldBe(DeviceClasses.Light);
        device.Manufacturer.ShouldBe("Philips");
        device.Model.ShouldBe("9290022166");
        device.Firmware.ShouldBe("1.104.2");
        device.Connections["zigbee"].ShouldBe("0x0017880100000001");
        device.Capabilities().ShouldBe([CapabilityIds.Switch, CapabilityIds.Level, CapabilityIds.Color, CapabilityIds.Generic]);
        device.Constraint(CapabilityIds.Color, "colorTemperature").ShouldBe(new AttributeConstraint(Min: 2000, Max: 6535));

        var generic = device.Generic();
        generic.Attributes.Select(a => a.Key).ShouldBe(["powerOnBehavior", "linkquality"]);
        generic.FindAttribute("powerOnBehavior")!.Writable.ShouldBeTrue();
        generic.FindAttribute("powerOnBehavior")!.Schema.ShouldBe(new EnumSchema(["off", "on", "toggle", "previous"]));
        generic.FindAttribute("linkquality")!.Category.ShouldBe(AttributeCategory.Diagnostic);
        generic.FindSetter("powerOnBehavior")!.Name.ShouldBe("setPowerOnBehavior");
        generic.FindCommand("setEffect").ShouldNotBeNull(); // write-only expose: a command without an attribute
    }

    [Fact]
    public void Light_state_converts_brightness_mireds_and_xy_to_canonical_units()
    {
        var light = _devices["living_room/ceiling"];
        var readings = light.ReadState(MqttFixtures.Parse("""
            {"state":"ON","brightness":127,"color_temp":250,"color":{"x":0.7006,"y":0.2993},"color_mode":"xy","linkquality":120,"power_on_behavior":"previous"}
            """));

        readings.Value(CapabilityIds.Switch, "on").ShouldBe(StateValue.True);
        readings.Value(CapabilityIds.Level, "level").ShouldBe(StateValue.Number(50));
        readings.Value(CapabilityIds.Color, "colorTemperature").ShouldBe(StateValue.Number(4000));
        var hue = readings.Value(CapabilityIds.Color, "hue")!.N!.Value;
        (hue < 1 || hue > 359).ShouldBeTrue($"hue {hue} is red"); // the red primary of the wide gamut
        readings.Value(CapabilityIds.Color, "saturation").ShouldBe(StateValue.Number(100));
        readings.Value(CapabilityIds.Generic, "linkquality").ShouldBe(StateValue.Number(120));
        readings.Value(CapabilityIds.Generic, "powerOnBehavior").ShouldBe(StateValue.String("previous"));
        light.Discovered.ShouldAllFit(readings);
    }

    [Fact]
    public void Light_commands_become_set_payloads_merged_per_batch()
    {
        var light = _devices["living_room/ceiling"];

        Payload(light.Publishes(CapabilityIds.Switch, "turnOn")).ShouldBe("""{"state":"ON"}""");
        Payload(light.Publishes(CapabilityIds.Switch, "toggle")).ShouldBe("""{"state":"TOGGLE"}""");
        Payload(light.Publishes(CapabilityIds.Level, "setLevel", """{"level":50,"transitionMs":1500}""")).ShouldBe("""{"brightness":127,"transition":1.5}""");
        Payload(light.Publishes(CapabilityIds.Color, "setColorTemperature", """{"colorTemperature":2700}""")).ShouldBe("""{"color_temp":370}""");
        var xy = MqttFixtures.Parse(light.Publishes(CapabilityIds.Color, "setColor", """{"hue":240,"saturation":100}""").Single().Payload);
        xy.GetProperty("color").GetProperty("x").GetDouble().ShouldBeInRange(0.13, 0.17); // the blue primary of the wide gamut
        light.Publishes(CapabilityIds.Generic, "setPowerOnBehavior", """{"powerOnBehavior":"on"}""").Single().Payload.ShouldBe("""{"power_on_behavior":"on"}""");
        light.Publishes(CapabilityIds.Generic, "setEffect", """{"effect":"blink"}""").Single().Payload.ShouldBe("""{"effect":"blink"}""");

        var (plan, rejection, _) = light.Plan(new DeviceCommandBatch(
            light.ExternalId,
            [
                new("main", CapabilityIds.Switch, "turnOn", MqttFixtures.Parse("{}")),
                new("main", CapabilityIds.Level, "setLevel", MqttFixtures.Parse("""{"level":100}""")),
            ],
            "k"));
        rejection.ShouldBeNull();
        plan!.Publishes.ShouldBe([new MqttPublish("zigbee2mqtt/living_room/ceiling/set", """{"state":"ON","brightness":254}""")]);
        plan.CompletesOnPublish.ShouldBeFalse();
    }

    [Fact]
    public void An_hs_light_is_commanded_with_hue_and_saturation()
    {
        var strip = _devices["led_strip"];

        Payload(strip.Publishes(CapabilityIds.Color, "setColor", """{"hue":120,"saturation":80}""")).ShouldBe("""{"color":{"hue":120,"saturation":80}}""");
        var readings = strip.ReadState(MqttFixtures.Parse("""{"color":{"hue":200,"saturation":55}}"""));
        readings.Value(CapabilityIds.Color, "hue").ShouldBe(StateValue.Number(200));
        readings.Value(CapabilityIds.Color, "saturation").ShouldBe(StateValue.Number(55));
        strip.Rejection(CapabilityIds.Color, "setColorTemperature", """{"colorTemperature":3000}""")!
            .ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe("unsupported_command");
    }

    [Fact]
    public void Composite_list_and_unknown_exposes_become_read_only_generic_json()
    {
        var strip = _devices["led_strip"];
        var generic = strip.Discovered.Generic();

        generic.Attributes.Select(a => (a.Key, a.Schema)).ShouldBe(
        [
            ("schedule", (ValueSchema)JsonValueSchema.Instance),
            ("scenes", JsonValueSchema.Instance),
            ("mystery", JsonValueSchema.Instance),
        ]);
        generic.Attributes.ShouldAllBe(a => !a.Writable);
        var readings = strip.ReadState(MqttFixtures.Parse("""{"schedule":{"hour":7},"scenes":["a","b"],"mystery":42}"""));
        readings.Value(CapabilityIds.Generic, "schedule")!.Kind.ShouldBe(StateValueKind.Json);
        readings.Value(CapabilityIds.Generic, "mystery").ShouldBe(StateValue.Number(42));
    }

    [Fact]
    public void A_metering_plug_maps_power_energy_voltage_and_current()
    {
        var plug = _devices["kitchen_plug"];
        var device = plug.Discovered;

        device.DeviceClass.ShouldBe(DeviceClasses.Outlet);
        device.Capabilities().ShouldBe([CapabilityIds.Switch, CapabilityIds.Power, CapabilityIds.Energy, CapabilityIds.Generic]);
        device.Generic().Attributes.Select(a => (a.Key, a.Unit, a.StateClass, a.Recorded)).ShouldBe(
        [
            ("voltage", "V", StateClass.Measurement, true),
            ("current", "A", StateClass.Measurement, true),
            ("linkquality", "lqi", StateClass.Measurement, true),
        ]);

        var readings = plug.ReadState(MqttFixtures.Parse("""{"state":"OFF","power":12.5,"energy":1.25,"voltage":229.8,"current":0.06}"""));
        readings.Value(CapabilityIds.Switch, "on").ShouldBe(StateValue.False);
        readings.Value(CapabilityIds.Power, "power").ShouldBe(StateValue.Number(12.5));
        readings.Value(CapabilityIds.Energy, "energy").ShouldBe(StateValue.Number(1250)); // kWh → Wh
        readings.Value(CapabilityIds.Generic, "voltage").ShouldBe(StateValue.Number(229.8));
        readings.Value(CapabilityIds.Generic, "current").ShouldBe(StateValue.Number(0.06));
        device.ShouldAllFit(readings);
        plug.Refresh().Single().ShouldBe(new MqttPublish("zigbee2mqtt/kitchen_plug/get", """{"state":""}"""));
    }

    [Fact]
    public void Sensors_map_to_their_capabilities_and_battery_voltage_to_volts()
    {
        var climate = _devices["bedroom_climate"];
        climate.Discovered.DeviceClass.ShouldBe(DeviceClasses.Sensor);
        climate.Discovered.Capabilities().ShouldBe([CapabilityIds.Temperature, CapabilityIds.Humidity, CapabilityIds.Battery, CapabilityIds.Generic]);
        climate.Discovered.Generic().Attributes.Select(a => a.Key).ShouldBe(["pressure", "voltage"]);
        climate.Refresh().ShouldBeEmpty(); // battery devices sleep: never asked

        var readings = climate.ReadState(MqttFixtures.Parse("""{"temperature":21.37,"humidity":48.5,"pressure":1013.2,"battery":97.4,"voltage":3005}"""));
        readings.Value(CapabilityIds.Temperature, "temperature").ShouldBe(StateValue.Number(21.37));
        readings.Value(CapabilityIds.Humidity, "humidity").ShouldBe(StateValue.Number(48.5));
        readings.Value(CapabilityIds.Battery, "battery").ShouldBe(StateValue.Number(97));
        readings.Value(CapabilityIds.Generic, "voltage").ShouldBe(StateValue.Number(3.005));
        climate.Discovered.ShouldAllFit(readings);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_contact_sensor_reports_open_when_the_magnet_is_away(bool contact, bool open)
    {
        var door = _devices["front_door"];
        door.Discovered.Capabilities().ShouldBe([CapabilityIds.Contact, CapabilityIds.Battery, CapabilityIds.Generic]);

        var readings = door.ReadState(MqttFixtures.Parse($$"""{"contact":{{(contact ? "true" : "false")}},"battery_low":false}"""));

        readings.Value(CapabilityIds.Contact, "open").ShouldBe(StateValue.Bool(open));
        readings.Value(CapabilityIds.Generic, "batteryLow").ShouldBe(StateValue.False);
    }

    [Fact]
    public void Occupancy_illuminance_water_leak_and_smoke_are_mapped()
    {
        var motion = _devices["hallway_motion"];
        var leak = _devices["laundry_leak"];

        motion.Discovered.Capabilities().ShouldBe([CapabilityIds.Occupancy, CapabilityIds.Battery, CapabilityIds.Generic]);
        var readings = motion.ReadState(MqttFixtures.Parse("""{"occupancy":true,"illuminance":87}"""));
        readings.Value(CapabilityIds.Occupancy, "occupied").ShouldBe(StateValue.True);
        readings.Value(CapabilityIds.Generic, "illuminance").ShouldBe(StateValue.Number(87));
        motion.Discovered.Generic().FindAttribute("illuminance")!.Unit.ShouldBe("lx");

        leak.Discovered.Capabilities().ShouldBe([CapabilityIds.WaterLeak, CapabilityIds.Smoke, CapabilityIds.Battery]);
        var alarms = leak.ReadState(MqttFixtures.Parse("""{"water_leak":true,"smoke":false}"""));
        alarms.Value(CapabilityIds.WaterLeak, "leak").ShouldBe(StateValue.True);
        alarms.Value(CapabilityIds.Smoke, "smoke").ShouldBe(StateValue.False);
    }

    [Fact]
    public void A_lock_maps_lock_state_and_its_commands()
    {
        var doorLock = _devices["front_door_lock"];

        doorLock.Discovered.DeviceClass.ShouldBe(DeviceClasses.Lock);
        doorLock.Discovered.Capabilities().ShouldBe([CapabilityIds.Lock, CapabilityIds.Battery]);
        doorLock.ReadState(MqttFixtures.Parse("""{"lock_state":"locked","state":"LOCK"}""")).Value(CapabilityIds.Lock, "lockState").ShouldBe(StateValue.String("locked"));
        doorLock.ReadState(MqttFixtures.Parse("""{"lock_state":"not_fully_locked"}""")).Value(CapabilityIds.Lock, "lockState").ShouldBe(StateValue.String("jammed"));
        Payload(doorLock.Publishes(CapabilityIds.Lock, "lock")).ShouldBe("""{"state":"LOCK"}""");
        Payload(doorLock.Publishes(CapabilityIds.Lock, "unlock")).ShouldBe("""{"state":"UNLOCK"}""");
        doorLock.Plan(new DeviceCommandBatch(doorLock.ExternalId, [new("main", CapabilityIds.Lock, "unlock", MqttFixtures.Parse("{}"))], "k"))
            .SafetyCritical.ShouldBeTrue();
    }

    [Fact]
    public void Covers_map_state_position_and_commands_and_a_garage_opener_is_safety_critical()
    {
        var blind = _devices["office_blind"];
        var garage = _devices["garage_door"];

        blind.Discovered.DeviceClass.ShouldBe(DeviceClasses.Curtain);
        var readings = blind.ReadState(MqttFixtures.Parse("""{"state":"CLOSE","position":30}"""));
        readings.Value(CapabilityIds.Cover, "coverState").ShouldBe(StateValue.String("closed"));
        readings.Value(CapabilityIds.Cover, "position").ShouldBe(StateValue.Number(30));
        Payload(blind.Publishes(CapabilityIds.Cover, "stop")).ShouldBe("""{"state":"STOP"}""");
        Payload(blind.Publishes(CapabilityIds.Cover, "setPosition", """{"position":75}""")).ShouldBe("""{"position":75}""");

        garage.Discovered.DeviceClass.ShouldBe(DeviceClasses.Garage);
        garage.Plan(new DeviceCommandBatch(garage.ExternalId, [new("main", CapabilityIds.Cover, "open", MqttFixtures.Parse("{}"))], "k"))
            .SafetyCritical.ShouldBeTrue();
        garage.Rejection(CapabilityIds.Cover, "setPosition", """{"position":50}""").ShouldNotBeNull(); // no position feature
    }

    [Fact]
    public void A_radiator_valve_maps_to_a_constrained_thermostat()
    {
        var trv = _devices["bedroom_trv"];
        var device = trv.Discovered;

        device.DeviceClass.ShouldBe(DeviceClasses.Thermostat);
        device.Capabilities().ShouldBe([CapabilityIds.Thermostat, CapabilityIds.Battery, CapabilityIds.Generic]);
        device.Constraint(CapabilityIds.Thermostat, "mode")!.Values.ShouldBe(["off", "heat", "auto"]);
        device.Constraint(CapabilityIds.Thermostat, "targetTemperature").ShouldBe(new AttributeConstraint(Min: 5, Max: 30, Step: 0.5));
        device.Generic().FindSetter("localTemperatureCalibration").ShouldNotBeNull();

        var readings = trv.ReadState(MqttFixtures.Parse("""{"system_mode":"heat","occupied_heating_setpoint":21.5,"local_temperature":19.8,"running_state":"heat"}"""));
        readings.Value(CapabilityIds.Thermostat, "mode").ShouldBe(StateValue.String("heat"));
        readings.Value(CapabilityIds.Thermostat, "targetTemperature").ShouldBe(StateValue.Number(21.5));
        readings.Value(CapabilityIds.Thermostat, "currentTemperature").ShouldBe(StateValue.Number(19.8));
        readings.Value(CapabilityIds.Generic, "runningState").ShouldBe(StateValue.String("heat"));
        device.ShouldAllFit(readings);

        Payload(trv.Publishes(CapabilityIds.Thermostat, "setMode", """{"mode":"auto"}""")).ShouldBe("""{"system_mode":"auto"}""");
        Payload(trv.Publishes(CapabilityIds.Thermostat, "setTargetTemperature", """{"targetTemperature":22.5}""")).ShouldBe("""{"occupied_heating_setpoint":22.5}""");
        trv.Rejection(CapabilityIds.Thermostat, "setMode", """{"mode":"cool"}""").ShouldNotBeNull(); // outside the constraint
        trv.Rejection(CapabilityIds.Thermostat, "setTargetTemperature", """{"targetTemperature":32}""").ShouldNotBeNull();
    }

    [Fact]
    public void The_action_property_becomes_button_events_and_generic_actions()
    {
        var remote = _devices["desk_remote"];
        var device = remote.Discovered;

        device.DeviceClass.ShouldBe(DeviceClasses.Button);
        device.Capabilities().ShouldBe([CapabilityIds.Button, CapabilityIds.Battery, CapabilityIds.Generic]);
        device.Generic().FindAttribute("action")!.Schema.ShouldBe(new MomentarySchema { Events = ["triple", "quadruple", "release"] });

        Event(remote.ReadState(MqttFixtures.Parse("""{"action":"single"}"""))).ShouldBe(("button", "press", "single"));
        Event(remote.ReadState(MqttFixtures.Parse("""{"action":"double"}"""))).ShouldBe(("button", "press", "double"));
        Event(remote.ReadState(MqttFixtures.Parse("""{"action":"hold"}"""))).ShouldBe(("button", "press", "long"));
        Event(remote.ReadState(MqttFixtures.Parse("""{"action":"triple"}"""))).ShouldBe(("generic", "action", "triple"));
        remote.ReadState(MqttFixtures.Parse("""{"action":""}""")).OfType<MqttReading.Event>().ShouldBeEmpty();
    }

    [Fact]
    public void Expose_endpoints_become_endpoints()
    {
        var gang = _devices["hall_2gang"];
        var device = gang.Discovered;

        device.Endpoints.Select(e => e.Key).ShouldBe(["left", "right", "main"]);
        device.Capabilities("left").ShouldBe([CapabilityIds.Switch]);
        device.Capabilities("right").ShouldBe([CapabilityIds.Switch]);
        device.Capabilities("main").ShouldBe([CapabilityIds.Power]);

        var readings = gang.ReadState(MqttFixtures.Parse("""{"state_left":"ON","state_right":"OFF","power":4.2}"""));
        readings.Value("left", CapabilityIds.Switch, "on").ShouldBe(StateValue.True);
        readings.Value("right", CapabilityIds.Switch, "on").ShouldBe(StateValue.False);
        Payload(gang.Publishes(CapabilityIds.Switch, "turnOn", endpoint: "right")).ShouldBe("""{"state_right":"ON"}""");
        gang.Rejection(CapabilityIds.Switch, "turnOn", endpoint: "main")!.ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe("unsupported_command");
        gang.Rejection(CapabilityIds.Switch, "turnOn", endpoint: "nope")!.ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe("unknown_endpoint");
    }

    [Fact]
    public void The_bridge_device_reports_permit_join_and_opens_it_on_command()
    {
        var bridge = Z2mDeviceMapper.MapBridge(Bridge, MqttFixtures.Json("z2m-bridge-info.json"), Base);

        bridge.IsBridge.ShouldBeTrue();
        bridge.Discovered.Name.ShouldBe("Zigbee2MQTT bridge");
        bridge.Discovered.Firmware.ShouldBe("2.14.2");
        bridge.Discovered.Model.ShouldBe("zStack3x0");
        bridge.Topics.ShouldBe(["zigbee2mqtt/bridge/info", "zigbee2mqtt/bridge/state"]);
        var readings = bridge.ReadState(MqttFixtures.Json("z2m-bridge-info.json"));
        readings.Value(CapabilityIds.Generic, "permitJoin").ShouldBe(StateValue.False);
        readings.Value(CapabilityIds.Generic, "version").ShouldBe(StateValue.String("2.14.2"));
        bridge.Publishes(CapabilityIds.Generic, "permitJoin", """{"seconds":120}""").ShouldBe(
            [new MqttPublish("zigbee2mqtt/bridge/request/permit_join", """{"time":120}""")]);
    }

    [Theory]
    [InlineData("""{"state":"online"}""", Availability.Online)]
    [InlineData("""{"state":"offline"}""", Availability.Offline)]
    [InlineData("online", Availability.Online)]
    [InlineData("offline", Availability.Offline)]
    public void Availability_accepts_json_and_plain_payloads(string payload, Availability expected)
    {
        var plug = _devices["kitchen_plug"];

        var readings = plug.Read(MqttMessage.FromText("zigbee2mqtt/kitchen_plug/availability", payload));

        readings.ShouldBe([new MqttReading.Reachability(expected)]);
    }

    [Fact]
    public void Every_fixture_device_fits_the_inventory_rules()
    {
        foreach (var device in _devices.Values.Select(d => d.Discovered))
        {
            device.Endpoints.ShouldNotBeEmpty(device.Name);
            device.Endpoints.Select(e => e.Key).ShouldBeUnique(device.Name);
            foreach (var endpoint in device.Endpoints)
            {
                endpoint.Capabilities.Select(c => c.CapabilityId).ShouldBeUnique($"{device.Name}/{endpoint.Key}");
                endpoint.Capabilities.ShouldAllBe(c => (c.CapabilityId == CapabilityIds.Generic) == (c.GenericDefinition != null));
                endpoint.Capabilities.ShouldAllBe(c => CapabilityCatalog.Find(c.CapabilityId) != null);
                foreach (var generic in endpoint.Capabilities.Select(c => c.GenericDefinition).OfType<CapabilityDefinition>())
                {
                    generic.Attributes.Where(a => a.Writable).ShouldAllBe(a => generic.FindSetter(a.Key) != null);
                }
            }
        }
    }

    private static string Payload(IReadOnlyList<MqttPublish> publishes)
    {
        publishes.Count.ShouldBe(1);
        publishes[0].Topic.ShouldEndWith("/set");
        return publishes[0].Payload;
    }

    private static (string Capability, string Attribute, string Event) Event(IReadOnlyList<MqttReading> readings)
    {
        var single = readings.OfType<MqttReading.Event>().ShouldHaveSingleItem();
        return (single.Capability, single.Attribute, single.Name);
    }
}


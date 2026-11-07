using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.LgThinQ.Model;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;

namespace ControlHome.Connectors.Tests.LgThinQ;

/// <summary>Profiles (Home Assistant lg_thinq fixtures and synthetic ones after pythinqconnect) → capabilities, readings and control payloads.</summary>
public sealed class LgDeviceMapperTests
{
    [Fact]
    public void An_air_conditioner_gets_a_switch_a_thermostat_and_its_sensors()
    {
        var ac = LgFixtures.Model("test-ac-0001", "ac", "ac.energy-profile.json");

        ac.Discovered.ShouldSatisfyAllConditions(
            d => d.Name.ShouldBe("Living room AC"),
            d => d.DeviceClass.ShouldBe(DeviceClasses.AirConditioner),
            d => d.Manufacturer.ShouldBe("LG"),
            d => d.Model.ShouldBe("PAC_910604_WW"),
            d => d.Endpoints.Select(e => e.Key).ShouldBe(["main"]));
        ac.Capabilities().ShouldBe([CapabilityIds.Switch, CapabilityIds.Thermostat, CapabilityIds.Humidity, CapabilityIds.AirQuality, CapabilityIds.Generic]);
        ac.Constraint(CapabilityIds.Thermostat, "mode")!.Values.ShouldBe(["off", "cool", "dry", "fan"]);
        var target = ac.Constraint(CapabilityIds.Thermostat, "targetTemperature")!;
        (target.Min, target.Max, target.Step, target.DependsOn).ShouldBe((18, 30, 1, "mode"));
        target.Cases!.Keys.ShouldBe(["cool"]);
        ac.Constraint(CapabilityIds.Thermostat, "fanMode")!.Values.ShouldBe(["auto", "low", "medium", "high"]);
        ac.Energy.ShouldBe([("main", "energyUsage")]);

        var generic = ac.Generic();
        generic.Attributes.Select(a => a.Key).ShouldContain("PM10");
        generic.Attributes.Select(a => a.Key).ShouldContain("powerSaveEnabled");
        generic.Attributes.Select(a => a.Key).ShouldNotContain("windStrength"); // mapped to fanMode
        generic.Attributes.Select(a => a.Key).ShouldNotContain("currentJobMode"); // every job mode is a thermostat mode
        generic.FindCommand("setAirCleanOperationMode").ShouldNotBeNull();
        generic.FindAttribute(LgDeviceModel.NotificationAttribute)!.Schema.ShouldBe(new MomentarySchema { Events = ["water_is_full"] });

        var readings = ac.Read(LgFixtures.Json("ac.status.json"));
        ac.ShouldAllFit(readings);
        readings.Value(CapabilityIds.Switch, "on").ShouldBe(StateValue.True);
        readings.Value(CapabilityIds.Thermostat, "mode").ShouldBe(StateValue.String("cool"));
        readings.Value(CapabilityIds.Thermostat, "targetTemperature").ShouldBe(StateValue.Number(19));
        readings.Value(CapabilityIds.Thermostat, "currentTemperature").ShouldBe(StateValue.Number(25));
        readings.Value(CapabilityIds.Thermostat, "fanMode").ShouldBe(StateValue.String("medium"));
        readings.Value(CapabilityIds.Humidity, "humidity").ShouldBe(StateValue.Number(40));
        readings.Value(CapabilityIds.AirQuality, "pm25").ShouldBe(StateValue.Number(24));
        readings.Value(CapabilityIds.Generic, "powerSaveEnabled").ShouldBe(StateValue.False);
    }

    [Fact]
    public void Air_conditioner_commands_become_the_sdks_control_payloads()
    {
        var ac = LgFixtures.Model("test-ac-0001", "ac");
        var on = LgFixtures.Json("ac.status.json");
        var off = LgFixtures.Parse("""{"operation":{"airConOperationMode":"POWER_OFF"},"airConJobMode":{"currentJobMode":"COOL"}}""");

        ac.Calls(on, ("main", CapabilityIds.Thermostat, "setTargetTemperature", """{"targetTemperature":24}"""))
            .ShouldBe(["""{"temperatureInUnits":{"coolTargetTemperature":24,"unit":"C"}}"""]);
        ac.Calls(on, ("main", CapabilityIds.Thermostat, "setMode", """{"mode":"off"}"""))
            .ShouldBe(["""{"operation":{"airConOperationMode":"POWER_OFF"}}"""]);
        ac.Calls(off, ("main", CapabilityIds.Thermostat, "setMode", """{"mode":"dry"}"""))
            .ShouldBe(["""{"operation":{"airConOperationMode":"POWER_ON"}}""", """{"airConJobMode":{"currentJobMode":"AIR_DRY"}}"""]);
        ac.Calls(on, ("main", CapabilityIds.Switch, "toggle", "{}")).ShouldBe(["""{"operation":{"airConOperationMode":"POWER_OFF"}}"""]);
        ac.Calls(on, ("main", CapabilityIds.Generic, "setPowerSaveEnabled", """{"powerSaveEnabled":true}"""))
            .ShouldBe(["""{"powerSave":{"powerSaveEnabled":true}}"""]);

        // Dependent properties of one batch go out in one control call.
        ac.Calls(
                on,
                ("main", CapabilityIds.Thermostat, "setFanMode", """{"fanMode":"high"}"""),
                ("main", CapabilityIds.Thermostat, "setTargetTemperature", """{"targetTemperature":22}"""))
            .ShouldBe(["""{"airFlow":{"windStrength":"HIGH"},"temperatureInUnits":{"coolTargetTemperature":22,"unit":"C"}}"""]);

        ac.Rejection(on, "main", CapabilityIds.Thermostat, "setTargetTemperature", """{"targetTemperature":31}""")
            .ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe(CommandRejectionCodes.InvalidArguments);
        ac.Rejection(on, "main", CapabilityIds.Thermostat, "setMode", """{"mode":"heat"}""")
            .ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe(CommandRejectionCodes.InvalidArguments); // not in the mode constraint
        ac.Rejection(on, "main", CapabilityIds.Lock, "unlock")
            .ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe(CommandRejectionCodes.UnsupportedCommand);
    }

    [Fact]
    public void A_washer_gets_an_appliance_cycle_gated_by_its_remote_start_switch()
    {
        var washer = LgFixtures.Model("test-washer-0002", "washer");

        washer.Discovered.DeviceClass.ShouldBe(DeviceClasses.Washer);
        washer.Capabilities().ShouldBe([CapabilityIds.ApplianceCycle, CapabilityIds.Generic]);
        var generic = washer.Generic();
        generic.Attributes.Select(a => a.Key).ShouldBe(
            ["cycleCount", "detergentSetting", "washerOperationMode", "relativeHourToStart", "relativeMinuteToStart", "totalHour", "totalMinute", LgDeviceModel.NotificationAttribute]);
        generic.FindAttribute("relativeHourToStart")!.Writable.ShouldBeTrue();
        generic.FindAttribute("washerOperationMode")!.Schema.ShouldBe(new EnumSchema(["START", "STOP", "POWER_OFF", "POWER_ON"]));

        var status = LgFixtures.Json("washer.status.json");
        var readings = washer.Read(status);
        washer.ShouldAllFit(readings);
        readings.Value(CapabilityIds.ApplianceCycle, "machineState").ShouldBe(StateValue.String("idle"));
        readings.Value(CapabilityIds.ApplianceCycle, "phase").ShouldBe(StateValue.String("power_off"));
        readings.Value(CapabilityIds.ApplianceCycle, "remainingMinutes").ShouldBe(StateValue.Number(45));
        readings.Value(CapabilityIds.ApplianceCycle, "remoteControlEnabled").ShouldBe(StateValue.False);
        readings.Value(CapabilityIds.Generic, "detergentSetting").ShouldBe(StateValue.String("NORMAL"));

        washer.Calls(status, ("main", CapabilityIds.ApplianceCycle, "start", "{}"))
            .ShouldBe(["""{"location":{"locationName":"MAIN"},"operation":{"washerOperationMode":"START"}}"""]);
        washer.Calls(status, ("main", CapabilityIds.ApplianceCycle, "pause", "{}"))
            .ShouldBe(["""{"location":{"locationName":"MAIN"},"operation":{"washerOperationMode":"STOP"}}"""]);
        washer.Calls(status, ("main", CapabilityIds.ApplianceCycle, "stop", "{}"))
            .ShouldBe(["""{"location":{"locationName":"MAIN"},"operation":{"washerOperationMode":"POWER_OFF"}}"""]);
        washer.Calls(status, ("main", CapabilityIds.Generic, "setRelativeHourToStart", """{"relativeHourToStart":3}"""))
            .ShouldBe(["""{"location":{"locationName":"MAIN"},"timer":{"relativeHourToStart":3}}"""]);
        washer.Rejection(status, "main", CapabilityIds.Generic, "setRelativeHourToStart", """{"relativeHourToStart":0}""")
            .ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe(CommandRejectionCodes.InvalidArguments);
    }

    [Fact]
    public void A_washtower_has_one_endpoint_per_sub_device()
    {
        var tower = LgFixtures.Model("test-tower-0004", "washtower", "ac.energy-profile.json");

        tower.Discovered.Endpoints.Select(e => (e.Key, e.Name)).ShouldBe([("washer", "Washer"), ("dryer", "Dryer")]);
        tower.Capabilities("washer").ShouldBe([CapabilityIds.ApplianceCycle, CapabilityIds.Generic]);
        tower.Capabilities("dryer").ShouldBe([CapabilityIds.ApplianceCycle, CapabilityIds.Generic]);
        tower.NotificationEndpoint("DRYING_IS_COMPLETE").ShouldBe("dryer");
        tower.NotificationEndpoint("WASHING_IS_COMPLETE").ShouldBe("washer");
        tower.Energy.ShouldBe([("washer", "energyUsage")]);

        var status = LgFixtures.Json("washtower.status.json");
        var readings = tower.Read(status);
        tower.ShouldAllFit(readings);
        readings.Value(CapabilityIds.ApplianceCycle, "machineState", "washer").ShouldBe(StateValue.String("running"));
        readings.Value(CapabilityIds.ApplianceCycle, "remainingMinutes", "washer").ShouldBe(StateValue.Number(42));
        readings.Value(CapabilityIds.ApplianceCycle, "remoteControlEnabled", "dryer").ShouldBe(StateValue.False);

        tower.Calls(status, ("dryer", CapabilityIds.ApplianceCycle, "start", "{}")).ShouldBe(["""{"dryer":{"operation":{"dryerOperationMode":"START"}}}"""]);
        tower.Calls(
                status,
                ("washer", CapabilityIds.ApplianceCycle, "stop", "{}"),
                ("dryer", CapabilityIds.ApplianceCycle, "stop", "{}"))
            .ShouldBe(["""{"washer":{"operation":{"washerOperationMode":"POWER_OFF"}}}""", """{"dryer":{"operation":{"dryerOperationMode":"POWER_OFF"}}}"""]);
    }

    [Fact]
    public void Separate_washtower_devices_become_children_of_the_washer()
    {
        var list = LgDeviceInfo.ParseList(LgFixtures.Json("devices.json"));

        var parents = LgDeviceMapper.Parents(list);

        parents.ShouldBe(new Dictionary<string, string> { ["test-towerdryer-0006"] = "test-towerwasher-0005" });
        var dryer = LgDeviceMapper.Map(
            list.Single(d => d.DeviceId == "test-towerdryer-0006"),
            LgFixtures.Json("washtower-dryer.profile.json"),
            null,
            parents["test-towerdryer-0006"]);
        dryer.Discovered.ParentExternalId.ShouldBe("test-towerwasher-0005");
        dryer.Discovered.DeviceClass.ShouldBe(DeviceClasses.Dryer);
        var readings = dryer.Read(LgFixtures.Json("washtower-dryer.status.json"));
        dryer.ShouldAllFit(readings);
        readings.Value(CapabilityIds.ApplianceCycle, "remainingMinutes").ShouldBe(StateValue.Number(41));
        readings.Value(CapabilityIds.ApplianceCycle, "remoteControlEnabled").ShouldBe(StateValue.True);
    }

    [Fact]
    public void A_refrigerator_has_an_endpoint_per_compartment_and_a_door_contact()
    {
        var fridge = LgFixtures.Model("test-fridge-0003", "refrigerator");

        fridge.Discovered.Endpoints.Select(e => (e.Key, e.Name)).ShouldBe([("main", null), ("fridge", "Fridge"), ("freezer", "Freezer")]);
        fridge.Capabilities().ShouldBe([CapabilityIds.Contact, CapabilityIds.Generic]);
        fridge.Generic().Attributes.Select(a => a.Key).ShouldBe(["powerSaveEnabled", "expressMode", "rapidFreeze", "usedTime", LgDeviceModel.NotificationAttribute]);
        var target = fridge.Generic("freezer").FindAttribute("targetTemperature")!;
        target.Schema.ShouldBe(new NumberSchema(-24, -14, 1, Units.Celsius, Integer: true));
        target.Writable.ShouldBeTrue();

        var status = LgFixtures.Json("refrigerator.status.json");
        var readings = fridge.Read(status);
        fridge.ShouldAllFit(readings);
        readings.Value(CapabilityIds.Contact, "open").ShouldBe(StateValue.False);
        readings.Value(CapabilityIds.Generic, "targetTemperature", "fridge").ShouldBe(StateValue.Number(3));
        readings.Value(CapabilityIds.Generic, "targetTemperature", "freezer").ShouldBe(StateValue.Number(-18));
        readings.Value(CapabilityIds.Generic, "rapidFreeze").ShouldBe(StateValue.True);

        fridge.Calls(status, ("fridge", CapabilityIds.Generic, "setTargetTemperature", """{"targetTemperature":4}"""))
            .ShouldBe(["""{"temperatureInUnits":{"locationName":"FRIDGE","targetTemperatureC":4}}"""]);
        fridge.Calls(
                status,
                ("fridge", CapabilityIds.Generic, "setTargetTemperature", """{"targetTemperature":4}"""),
                ("freezer", CapabilityIds.Generic, "setTargetTemperature", """{"targetTemperature":-20}"""))
            .Count.ShouldBe(2); // one compartment per control call
    }

    [Fact]
    public void A_kimchi_refrigerator_maps_its_compartments_and_drops_values_outside_the_profile()
    {
        var kimchi = LgFixtures.Model("test-kimchi-0011", "kimchi");

        kimchi.Discovered.Endpoints.Select(e => e.Key).ShouldBe(["top", "middle"]);
        kimchi.NotificationEndpoint("DOOR_IS_OPEN").ShouldBe("top");
        var readings = kimchi.Read(LgFixtures.Json("kimchi.status.json"));
        kimchi.ShouldAllFit(readings);
        readings.Value(CapabilityIds.Generic, "targetTemperature", "middle").ShouldBe(StateValue.String("KIMCHI"));
        readings.Value(CapabilityIds.Generic, "targetTemperature", "top").ShouldBeNull(); // a number where the profile lists enum values
    }

    [Fact]
    public void A_dishwasher_gets_a_cycle_and_a_door_contact()
    {
        var dishwasher = LgFixtures.Model("test-dish-0007", "dishwasher");

        dishwasher.Capabilities().ShouldBe([CapabilityIds.ApplianceCycle, CapabilityIds.Contact, CapabilityIds.Generic]);
        var status = LgFixtures.Json("dishwasher.status.json");
        var readings = dishwasher.Read(status);
        dishwasher.ShouldAllFit(readings);
        readings.Value(CapabilityIds.ApplianceCycle, "machineState").ShouldBe(StateValue.String("running"));
        readings.Value(CapabilityIds.ApplianceCycle, "remainingMinutes").ShouldBe(StateValue.Number(65));
        readings.Value(CapabilityIds.Contact, "open").ShouldBe(StateValue.False);
        readings.Value(CapabilityIds.Generic, "currentDishWashingCourse").ShouldBe(StateValue.String("ECO"));

        dishwasher.Calls(status, ("main", CapabilityIds.ApplianceCycle, "pause", "{}"))
            .ShouldBe(["""{"operation":{"dishWasherOperationMode":"STOP"}}"""]);
        dishwasher.Rejection(status, "main", CapabilityIds.ApplianceCycle, "stop")
            .ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe(CommandRejectionCodes.UnsupportedCommand);
    }

    [Fact]
    public void Air_purifiers_and_dehumidifiers_get_a_switch_and_their_sensors()
    {
        var purifier = LgFixtures.Model("test-purifier-0008", "air-purifier");
        purifier.Capabilities().ShouldBe([CapabilityIds.Switch, CapabilityIds.Generic]);
        purifier.Read(LgFixtures.Json("air-purifier.status.json")).Value(CapabilityIds.Switch, "on").ShouldBe(StateValue.True);
        purifier.Calls(LgFixtures.Json("air-purifier.status.json"), ("main", CapabilityIds.Switch, "turnOff", "{}"))
            .ShouldBe(["""{"operation":{"airPurifierOperationMode":"POWER_OFF"}}"""]);

        var dehumidifier = LgFixtures.Model("test-dehum-0009", "dehumidifier");
        dehumidifier.Capabilities().ShouldBe([CapabilityIds.Humidity, CapabilityIds.Switch, CapabilityIds.Generic]);
        var readings = dehumidifier.Read(LgFixtures.Json("dehumidifier.status.json"));
        dehumidifier.ShouldAllFit(readings);
        readings.Value(CapabilityIds.Humidity, "humidity").ShouldBe(StateValue.Number(80));
        readings.Value(CapabilityIds.Generic, "targetHumidity").ShouldBe(StateValue.Number(55));
        dehumidifier.Generic().FindAttribute("targetHumidity")!.Schema.ShouldBe(new NumberSchema(30, 70, 5, null, Integer: true));
        dehumidifier.Calls(LgFixtures.Json("dehumidifier.status.json"), ("main", CapabilityIds.Generic, "setTargetHumidity", """{"targetHumidity":45}"""))
            .ShouldBe(["""{"humidity":{"targetHumidity":45}}"""]);
    }

    [Fact]
    public void A_robot_cleaner_starts_pauses_and_returns_home_without_a_remote_gate()
    {
        var robot = LgFixtures.Model("test-robot-0010", "robot-cleaner");

        robot.Discovered.DeviceClass.ShouldBe("vacuum");
        robot.Capabilities().ShouldBe([CapabilityIds.ApplianceCycle, CapabilityIds.Battery, CapabilityIds.Generic]);
        var status = LgFixtures.Json("robot-cleaner.status.json");
        var readings = robot.Read(status);
        robot.ShouldAllFit(readings);
        readings.Value(CapabilityIds.ApplianceCycle, "machineState").ShouldBe(StateValue.String("idle"));
        readings.Value(CapabilityIds.ApplianceCycle, "remoteControlEnabled").ShouldBe(StateValue.True);
        readings.Value(CapabilityIds.Battery, "battery").ShouldBe(StateValue.Number(87));
        robot.Calls(status, ("main", CapabilityIds.ApplianceCycle, "stop", "{}")).ShouldBe(["""{"operation":{"cleanOperationMode":"HOMING"}}"""]);
        robot.Calls(status, ("main", CapabilityIds.ApplianceCycle, "pause", "{}")).ShouldBe(["""{"operation":{"cleanOperationMode":"PAUSE"}}"""]);
    }

    [Theory]
    [InlineData("""{"property":{"mystery":{"level":{"mode":["r","w"],"type":"range","value":{"w":{"min":0,"max":3}}},"odd":{"mode":["r"],"type":"list"}}}}""")]
    [InlineData("""{"property":"nonsense"}""")]
    [InlineData("""{"property":{"r":{"":{"mode":["r"],"type":"number"},"x":{"mode":["r"],"type":"enum","value":{"r":[]}},"y":{"mode":["w"],"type":"range","value":{"w":{"min":5,"max":1}}}}}}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("""{}""")]
    public void Unknown_devices_and_payload_drift_degrade_to_generic_attributes_and_never_throw(string profile)
    {
        var info = new LgDeviceInfo("test-oven-0099", "DEVICE_FUTURE_GADGET", null, null, true, null);

        var model = LgDeviceMapper.Map(info, LgFixtures.Parse(profile), null);

        model.Discovered.Name.ShouldBe("DEVICE_FUTURE_GADGET");
        model.Discovered.DeviceClass.ShouldBe("future_gadget");
        var readings = model.Read(LgFixtures.Parse("""{"mystery":{"level":"not a number","odd":[1,{"a":2}]},"x":[]}"""));
        model.ShouldAllFit(readings);
        if (model.Discovered.Endpoints.Count > 0)
        {
            model.Generic().FindCommand("setLevel")?.Arguments.Single().Schema.ShouldBe(new NumberSchema(0, 3, 1, null, Integer: true));
        }
    }

    [Fact]
    public void Partial_reports_merge_into_the_status_newest_wins()
    {
        var status = new LgStatus();
        status.Replace(LgFixtures.Json("refrigerator.status.json"));

        var merged = status.Merge(LgFixtures.Parse(
            """{"temperatureInUnits":[{"locationName":"FREEZER","targetTemperatureC":-20}],"doorStatus":[{"locationName":"MAIN","doorState":"OPEN"}]}"""));

        var fridge = LgFixtures.Model("test-fridge-0003", "refrigerator");
        var readings = fridge.Read(merged);
        readings.Value(CapabilityIds.Generic, "targetTemperature", "freezer").ShouldBe(StateValue.Number(-20));
        readings.Value(CapabilityIds.Generic, "targetTemperature", "fridge").ShouldBe(StateValue.Number(3)); // untouched entry kept
        readings.Value(CapabilityIds.Contact, "open").ShouldBe(StateValue.True);
        readings.Value(CapabilityIds.Generic, "rapidFreeze").ShouldBe(StateValue.True); // untouched resource kept

        var tower = new LgStatus();
        tower.Merge(LgFixtures.Json("washtower.status.json"));
        var report = tower.Merge(LgFixtures.Parse("""{"dryer":{"runState":{"currentState":"RUNNING"}}}"""));
        var towerModel = LgFixtures.Model("test-tower-0004", "washtower");
        towerModel.Read(report).Value(CapabilityIds.ApplianceCycle, "machineState", "dryer").ShouldBe(StateValue.String("running"));
        towerModel.Read(report).Value(CapabilityIds.ApplianceCycle, "machineState", "washer").ShouldBe(StateValue.String("running"));
    }

    [Fact]
    public void A_full_state_asked_for_before_a_report_arrived_keeps_that_report()
    {
        var status = new LgStatus();
        status.Replace(LgFixtures.Parse("""{"operation":{"airConOperationMode":"POWER_ON"},"temperature":{"targetTemperature":19}}"""));
        var asked = status.Version;

        status.Merge(LgFixtures.Parse("""{"operation":{"airConOperationMode":"POWER_OFF"}}"""));
        var current = status.Replace(LgFixtures.Parse("""{"operation":{"airConOperationMode":"POWER_ON"},"temperature":{"targetTemperature":26}}"""), asked);

        current.GetProperty("operation").GetProperty("airConOperationMode").GetString().ShouldBe("POWER_OFF");
        current.GetProperty("temperature").GetProperty("targetTemperature").GetInt32().ShouldBe(26);

        // Without reports in between the full state replaces everything.
        var fresh = status.Replace(LgFixtures.Parse("""{"operation":{"airConOperationMode":"POWER_ON"}}"""), status.Version);
        fresh.GetProperty("operation").GetProperty("airConOperationMode").GetString().ShouldBe("POWER_ON");
        fresh.TryGetProperty("temperature", out _).ShouldBeFalse();
    }

    [Fact]
    public void A_read_rule_that_throws_skips_its_attribute_and_never_the_device()
    {
        var device = new DiscoveredDevice("test-x-0001", "X", null, "LG", null, null, null, new Dictionary<string, string>(), null, []);
        var model = new LgDeviceModel(
            "test-x-0001",
            "DEVICE_X",
            device,
            [
                new LgReadRule("main", CapabilityIds.Switch, "on", _ => throw new NullReferenceException("drift")),
                new LgReadRule("main", CapabilityIds.Switch, "on", _ => StateValue.True),
            ],
            new Dictionary<(string, string, string), LgCommandBuilder>(),
            [],
            new Dictionary<string, string>());

        model.Read(LgFixtures.Parse("{}")).ShouldHaveSingleItem().Value.ShouldBe(StateValue.True);
    }

    [Theory]
    [InlineData("END", false, "finished")]
    [InlineData("PAUSE", false, "paused")]
    [InlineData("RINSE_HOLD", false, "paused")]
    [InlineData("SPINNING", false, "running")]
    [InlineData("RESERVED", false, "idle")]
    [InlineData("ERROR", false, "error")]
    [InlineData("A_NEW_STATE", false, "running")]
    [InlineData("CHARGING", true, "idle")]
    [InlineData("HOMING", true, "running")]
    public void Run_states_map_to_machine_states(string state, bool robot, string expected) =>
        LgDeviceMapper.MachineState(state, robot).ShouldBe(expected);
}

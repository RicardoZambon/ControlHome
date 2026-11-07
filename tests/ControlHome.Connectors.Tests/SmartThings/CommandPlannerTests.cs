using System.Text.Json.Nodes;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.SmartThings.Mapping;
using ControlHome.Connectors.SmartThings.Model;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Connectors.Tests.SmartThings;

/// <summary>Canonical commands → SmartThings device commands (pure).</summary>
public sealed class CommandPlannerTests
{
    [Fact]
    public void Light_commands_use_the_component_and_the_vendor_scales()
    {
        var light = StFixtures.Map(StFixtures.Light);

        Plan(light, StFixtures.Command("main", CapabilityIds.Switch, "turnOn")).ShouldBe(["main/switch/on[]"]);
        Plan(light, StFixtures.Command("main", CapabilityIds.Switch, "toggle")).ShouldBe(["main/switch/on[]"]); // it is off
        Plan(light, StFixtures.Command("main", CapabilityIds.Level, "setLevel", """{"level":55}""")).ShouldBe(["main/switchLevel/setLevel[55]"]);
        Plan(light, StFixtures.Command("main", CapabilityIds.Color, "setColor", """{"hue":180,"saturation":50}"""))
            .ShouldBe(["""main/colorControl/setColor[{"hue":50,"saturation":50}]"""]);
        Plan(light, StFixtures.Command("main", CapabilityIds.Color, "setColorTemperature", """{"colorTemperature":2700}"""))
            .ShouldBe(["main/colorTemperature/setColorTemperature[2700]"]);
    }

    [Fact]
    public void An_air_conditioner_turns_on_before_it_changes_mode_and_off_through_its_switch()
    {
        var ac = StFixtures.Map(StFixtures.Ac);

        Plan(ac, StFixtures.Command("main", CapabilityIds.Thermostat, "setMode", """{"mode":"cool"}"""))
            .ShouldBe(["main/switch/on[]", """main/airConditionerMode/setAirConditionerMode["cool"]"""]);
        Plan(ac, StFixtures.Command("main", CapabilityIds.Thermostat, "setMode", """{"mode":"fan"}"""))
            .ShouldBe(["main/switch/on[]", """main/airConditionerMode/setAirConditionerMode["wind"]"""]);
        Plan(ac, StFixtures.Command("main", CapabilityIds.Thermostat, "setMode", """{"mode":"off"}""")).ShouldBe(["main/switch/off[]"]);
        Plan(ac, StFixtures.Command("main", CapabilityIds.Thermostat, "setTargetTemperature", """{"targetTemperature":22.5}"""))
            .ShouldBe(["main/thermostatCoolingSetpoint/setCoolingSetpoint[22.5]"]);
        Plan(ac, StFixtures.Command("main", CapabilityIds.Thermostat, "setFanMode", """{"fanMode":"high"}"""))
            .ShouldBe(["""main/airConditionerFanMode/setFanMode["high"]"""]);
    }

    [Fact]
    public void Fahrenheit_devices_get_their_setpoints_in_fahrenheit()
    {
        var thermostat = Build(
            """{"deviceId":"t-1","components":[{"id":"main","capabilities":[{"id":"thermostatMode","version":1},{"id":"thermostatHeatingSetpoint","version":1},{"id":"thermostatCoolingSetpoint","version":1},{"id":"temperatureMeasurement","version":1}],"categories":[{"name":"Thermostat","categoryType":"manufacturer"}]}]}""",
            """{"components":{"main":{"thermostatMode":{"thermostatMode":{"value":"heat"},"supportedThermostatModes":{"value":["off","heat","cool","auto"]}},"thermostatHeatingSetpoint":{"heatingSetpoint":{"value":68,"unit":"F"}},"thermostatCoolingSetpoint":{"coolingSetpoint":{"value":76,"unit":"F"}},"temperatureMeasurement":{"temperature":{"value":70,"unit":"F"}}}}}""");

        // Heating: the heating setpoint (20 °C reported for 68 °F), 21 °C sent as 70 °F.
        thermostat.Readings.Single(r => r.Attribute == "targetTemperature").Value.ShouldBe(StateValue.Number(20));
        Plan(thermostat, StFixtures.Command("main", CapabilityIds.Thermostat, "setTargetTemperature", """{"targetTemperature":21}"""))
            .ShouldBe(["main/thermostatHeatingSetpoint/setHeatingSetpoint[70]"]);
        Plan(thermostat, StFixtures.Command("main", CapabilityIds.Thermostat, "setMode", """{"mode":"off"}"""))
            .ShouldBe(["""main/thermostatMode/setThermostatMode["off"]"""]);
    }

    [Fact]
    public void Media_lock_appliance_and_generic_commands_map_to_their_capabilities()
    {
        var tv = StFixtures.Map(StFixtures.Tv);
        Plan(tv, StFixtures.Command("main", CapabilityIds.MediaPlayer, "selectSource", """{"source":"HDMI2"}""")).ShouldBe(["""main/mediaInputSource/setInputSource["HDMI2"]"""]);
        Plan(tv, StFixtures.Command("main", CapabilityIds.MediaPlayer, "setMuted", """{"muted":true}""")).ShouldBe(["main/audioMute/mute[]"]);
        Plan(tv, StFixtures.Command("main", CapabilityIds.MediaPlayer, "setVolume", """{"volume":20}""")).ShouldBe(["main/audioVolume/setVolume[20]"]);
        Plan(tv, StFixtures.Command("main", CapabilityIds.MediaPlayer, "powerOff")).ShouldBe(["main/switch/off[]"]);
        Plan(tv, StFixtures.Command("main", CapabilityIds.MediaPlayer, "next")).ShouldBe(["main/mediaTrackControl/nextTrack[]"]);

        var doorLock = StFixtures.Map(StFixtures.Lock);
        Plan(doorLock, StFixtures.Command("main", CapabilityIds.Lock, "unlock")).ShouldBe(["main/lock/unlock[]"]);

        var washer = StFixtures.Map(StFixtures.Washer, StFixtures.Definitions());
        Plan(washer, StFixtures.Command("main", CapabilityIds.ApplianceCycle, "start")).ShouldBe(["""main/washerOperatingState/setMachineState["run"]"""]);
        Plan(washer, StFixtures.Command("main", CapabilityIds.ApplianceCycle, "stop")).ShouldBe(["""main/washerOperatingState/setMachineState["stop"]"""]);
        Plan(washer, StFixtures.Command("main", CapabilityIds.Generic, "setWasherSpinLevel", """{"washerSpinLevel":"low"}"""))
            .ShouldBe(["""main/custom.washerSpinLevel/setWasherSpinLevel["low"]"""]);
        Plan(washer, StFixtures.Command("hca_main", CapabilityIds.Generic, "setMode", """{"washerMode":"quickWash"}"""))
            .ShouldBe(["""hca.main/hca.washerMode/setMode["quickWash"]"""]);
    }

    [Fact]
    public void Generic_setters_convert_back_to_the_vendor_unit_and_value_tables_pick_the_command()
    {
        var fridge = StFixtures.Map(StFixtures.Fridge, StFixtures.Definitions());

        Plan(fridge, StFixtures.Command("freezer", CapabilityIds.Generic, "setCoolingSetpoint", """{"coolingSetpoint":-20}"""))
            .ShouldBe(["freezer/thermostatCoolingSetpoint/setCoolingSetpoint[-4]"]);

        var main = fridge.Device.Endpoints.Single(e => e.Key == "main").Capabilities.Single(c => c.CapabilityId == CapabilityIds.Generic).GenericDefinition!;
        main.FindSetter("sabbathModeStatus")!.Name.ShouldBe("setSabbathModeStatus");
        Plan(fridge, StFixtures.Command("main", CapabilityIds.Generic, "setSabbathModeStatus", """{"sabbathModeStatus":"on"}"""))
            .ShouldBe(["main/samsungce.sabbathMode/on[]"]);
    }

    [Fact]
    public void Unknown_endpoints_unsupported_commands_and_unusable_arguments_are_rejected()
    {
        var light = StFixtures.Map(StFixtures.Light);

        Rejection(light, StFixtures.Command("l2", CapabilityIds.Switch, "turnOn")).ShouldBe(CommandRejectionCodes.UnknownEndpoint);
        Rejection(light, StFixtures.Command("main", CapabilityIds.Lock, "unlock")).ShouldBe(CommandRejectionCodes.UnsupportedCommand);
        Rejection(light, StFixtures.Command("main", CapabilityIds.Generic, "doSomething")).ShouldBe(CommandRejectionCodes.UnsupportedCommand);
        Rejection(light, StFixtures.Command("main", CapabilityIds.Level, "setLevel", """{"level":"high"}""")).ShouldBe(CommandRejectionCodes.InvalidArguments);

        var ac = StFixtures.Map(StFixtures.Ac);
        Rejection(ac, StFixtures.Command("main", CapabilityIds.Thermostat, "setFanMode", """{"fanMode":"quiet"}""")).ShouldBe(CommandRejectionCodes.InvalidArguments);
    }

    [Fact]
    public void Requests_hold_at_most_ten_commands_with_the_component_field()
    {
        var light = StFixtures.Map(StFixtures.Light);
        var commands = Enumerable.Range(0, 12).Select(i => StFixtures.Command("main", CapabilityIds.Switch, i % 2 == 0 ? "turnOn" : "turnOff")).ToArray();

        var planned = CommandPlanner.Plan(light, StFixtures.Batch(StFixtures.DeviceId(StFixtures.Light), "k", commands));
        var requests = CommandPlanner.Requests(planned.Commands);

        requests.Select(r => r.Count).ShouldBe([10, 2]);
        var body = CommandPlanner.Body(requests[1]);
        body.ToJsonString().ShouldBe("""{"commands":[{"component":"main","capability":"switch","command":"on","arguments":[]},{"component":"main","capability":"switch","command":"off","arguments":[]}]}""");
    }

    private static MappedDevice Build(string description, string status) =>
        SmartThingsDeviceMapper.Map(SmartThingsDevice.Parse(StFixtures.Parse(description))!, DeviceStatus.Parse(StFixtures.Parse(status)), StFixtures.Context())!;

    private static List<string> Plan(MappedDevice device, DeviceCommand command)
    {
        var planned = CommandPlanner.Plan(device, StFixtures.Batch(device.Device.ExternalId, "key", command));
        planned.Rejection.ShouldBeNull();
        return [.. planned.Commands.Select(c => $"{c.Component}/{c.Capability}/{c.Command}{new JsonArray([.. c.Arguments.Select(a => a?.DeepClone())]).ToJsonString()}")];
    }

    private static string Rejection(MappedDevice device, DeviceCommand command) =>
        CommandPlanner.Plan(device, StFixtures.Batch(device.Device.ExternalId, "key", command)).Rejection.ShouldBeOfType<CommandOutcome.Rejected>().Code;
}

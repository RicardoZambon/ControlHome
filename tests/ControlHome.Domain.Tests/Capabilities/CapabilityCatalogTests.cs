using System.Text.RegularExpressions;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;

namespace ControlHome.Domain.Tests.Capabilities;

public sealed partial class CapabilityCatalogTests
{
    public static TheoryData<string> CapabilityIdsInCatalog() => [.. CapabilityCatalog.All.Select(c => c.Id)];

    [Fact]
    public void Capability_ids_are_unique_and_match_the_constants()
    {
        var ids = CapabilityCatalog.All.Select(c => c.Id).ToList();

        ids.ShouldBeUnique();
        var constants = typeof(CapabilityIds).GetFields().Select(f => (string)f.GetRawConstantValue()!).ToList();
        ids.ShouldBe(constants, ignoreOrder: true);
    }

    [Fact]
    public void Lists_every_capability_of_the_architecture()
    {
        CapabilityCatalog.All.Select(c => c.Id).ShouldBe(
            [
                "switch", "level", "color", "thermostat", "power", "energy", "temperature", "humidity", "contact", "motion",
                "occupancy", "lock", "doorOpener", "cover", "alarmPanel", "mediaPlayer", "button", "battery", "presence", "smoke",
                "waterLeak", "airQuality", "camera", "applianceCycle", "generic",
            ],
            ignoreOrder: true);
    }

    [Theory]
    [MemberData(nameof(CapabilityIdsInCatalog))]
    public void Keys_names_and_arguments_are_camel_case_and_unique(string id)
    {
        var capability = CapabilityCatalog.Find(id)!;

        CamelCase().IsMatch(capability.Id).ShouldBeTrue(capability.Id);
        capability.Version.ShouldBeGreaterThanOrEqualTo(1);
        capability.Attributes.Select(a => a.Key).ShouldBeUnique();
        capability.Commands.Select(c => c.Name).ShouldBeUnique();
        foreach (var attribute in capability.Attributes)
        {
            CamelCase().IsMatch(attribute.Key).ShouldBeTrue($"{id}.{attribute.Key}");
        }

        foreach (var command in capability.Commands)
        {
            CamelCase().IsMatch(command.Name).ShouldBeTrue($"{id}.{command.Name}");
            command.Arguments.Select(a => a.Key).ShouldBeUnique();
            command.Arguments.ShouldAllBe(a => CamelCase().IsMatch(a.Key));
        }
    }

    [Theory]
    [MemberData(nameof(CapabilityIdsInCatalog))]
    public void Schemas_are_well_formed(string id)
    {
        var capability = CapabilityCatalog.Find(id)!;
        var schemas = capability.Attributes.Select(a => (Name: $"{id}.{a.Key}", a.Schema))
            .Concat(capability.Commands.SelectMany(c => c.Arguments.Select(a => (Name: $"{id}.{c.Name}({a.Key})", a.Schema))));

        foreach (var (name, schema) in schemas)
        {
            switch (schema)
            {
                case EnumSchema enumeration:
                    enumeration.Values.ShouldNotBeEmpty(name);
                    enumeration.Values.ShouldBeUnique(name);
                    break;
                case FlagsSchema flags:
                    flags.Flags.ShouldNotBeEmpty(name);
                    flags.Flags.ShouldBeUnique(name);
                    break;
                case NumberSchema { Min: { } min, Max: { } max }:
                    min.ShouldBeLessThan(max, name);
                    break;
                case MomentarySchema momentary:
                    momentary.Events.ShouldBeUnique(name);
                    break;
            }
        }
    }

    [Theory]
    [MemberData(nameof(CapabilityIdsInCatalog))]
    public void Attribute_units_match_their_number_schema(string id)
    {
        foreach (var attribute in CapabilityCatalog.Find(id)!.Attributes)
        {
            attribute.Unit.ShouldBe((attribute.Schema as NumberSchema)?.Unit, $"{id}.{attribute.Key}");
        }
    }

    [Theory]
    [MemberData(nameof(CapabilityIdsInCatalog))]
    public void Measurements_and_counters_are_numbers(string id)
    {
        foreach (var attribute in CapabilityCatalog.Find(id)!.Attributes.Where(a => a.StateClass != StateClass.None))
        {
            attribute.Schema.ShouldBeOfType<NumberSchema>($"{id}.{attribute.Key}");
            attribute.Unit.ShouldNotBeNull($"{id}.{attribute.Key}");
        }
    }

    [Theory]
    [MemberData(nameof(CapabilityIdsInCatalog))]
    public void Commands_reference_existing_attributes_and_are_never_both_critical_and_fail_safe(string id)
    {
        var capability = CapabilityCatalog.Find(id)!;
        foreach (var command in capability.Commands)
        {
            (command.SafetyCritical && command.FailSafe).ShouldBeFalse($"{id}.{command.Name}");
            (command.FailSafe && command.SafetyCriticalForDeviceClasses.Count > 0).ShouldBeFalse($"{id}.{command.Name}");
            if (command.GateAttribute is { } gate)
            {
                capability.FindAttribute(gate)?.Schema.ShouldBeOfType<BoolSchema>($"{id}.{command.Name} gate");
            }

            foreach (var argument in command.Arguments.Where(a => a.Attribute is not null))
            {
                capability.FindAttribute(argument.Attribute!).ShouldNotBeNull($"{id}.{command.Name}({argument.Key})");
            }

            if (command.Expects is { } expected)
            {
                var watched = capability.FindAttribute(expected.Attribute).ShouldNotBeNull($"{id}.{command.Name} expects");
                expected.Confirms.ShouldNotBeEmpty($"{id}.{command.Name} expects");
                foreach (var value in expected.Confirms.Concat(expected.Fails))
                {
                    ValueSchemaValidator.Accepts(watched.Schema, value).ShouldBeTrue($"{id}.{command.Name} expects {value}");
                }
            }
        }
    }

    [Fact]
    public void Exactly_the_architecture_commands_are_always_safety_critical()
    {
        var critical = CapabilityCatalog.All
            .SelectMany(c => c.Commands.Where(command => command.SafetyCritical).Select(command => $"{c.Id}.{command.Name}"))
            .ToList();

        critical.ShouldBe(["lock.unlock", "doorOpener.open", "alarmPanel.disarm"], ignoreOrder: true);
    }

    [Theory]
    [InlineData(DeviceClasses.Gate, true)]
    [InlineData(DeviceClasses.Garage, true)]
    [InlineData(DeviceClasses.Blind, false)]
    [InlineData(DeviceClasses.Curtain, false)]
    [InlineData(null, false)]
    public void Opening_a_cover_is_safety_critical_only_on_gates_and_garages(string? deviceClass, bool expected)
    {
        CapabilityCatalog.Cover.FindCommand("open")!.IsSafetyCriticalFor(deviceClass).ShouldBe(expected);
        CapabilityCatalog.Cover.FindCommand("setPosition")!.IsSafetyCriticalFor(deviceClass).ShouldBe(expected);
        CapabilityCatalog.Cover.FindCommand("close")!.IsSafetyCriticalFor(deviceClass).ShouldBeFalse();
        CapabilityCatalog.Cover.FindCommand("stop")!.IsSafetyCriticalFor(deviceClass).ShouldBeFalse();
    }

    [Fact]
    public void The_four_safety_critical_commands_are_flagged_and_the_safe_directions_are_fail_safe()
    {
        CapabilityCatalog.Lock.FindCommand("unlock")!.IsSafetyCriticalFor(null).ShouldBeTrue();
        CapabilityCatalog.DoorOpener.FindCommand("open")!.IsSafetyCriticalFor(null).ShouldBeTrue();
        CapabilityCatalog.AlarmPanel.FindCommand("disarm")!.IsSafetyCriticalFor(null).ShouldBeTrue();
        CapabilityCatalog.Cover.FindCommand("open")!.IsSafetyCriticalFor(DeviceClasses.Gate).ShouldBeTrue();

        CapabilityCatalog.Lock.FindCommand("lock")!.FailSafe.ShouldBeTrue();
        CapabilityCatalog.Cover.FindCommand("close")!.FailSafe.ShouldBeTrue();
        CapabilityCatalog.Lock.FindCommand("lock")!.IsSafetyCriticalFor(DeviceClasses.Lock).ShouldBeFalse();
    }

    [Fact]
    public void Appliance_cycle_commands_are_gated_by_remote_control()
    {
        CapabilityCatalog.ApplianceCycle.Commands.ShouldAllBe(c => c.GateAttribute == "remoteControlEnabled");
        CapabilityCatalog.ApplianceCycle.Commands.Select(c => c.Name).ShouldBe(["start", "pause", "stop"]);
    }

    [Fact]
    public void Defines_the_planned_attributes_and_commands()
    {
        Shape(CapabilityCatalog.Switch).ShouldBe("on | turnOn, turnOff, toggle");
        Shape(CapabilityCatalog.Level).ShouldBe("level | setLevel(level, transitionMs?)");
        Shape(CapabilityCatalog.Color).ShouldBe("hue, saturation, colorTemperature | setColor(hue, saturation), setColorTemperature(colorTemperature)");
        Shape(CapabilityCatalog.Thermostat).ShouldBe(
            "mode, targetTemperature, currentTemperature, fanMode | setMode(mode), setTargetTemperature(targetTemperature), setFanMode(fanMode)");
        Shape(CapabilityCatalog.Lock).ShouldBe("lockState | lock, unlock");
        Shape(CapabilityCatalog.DoorOpener).ShouldBe(" | open");
        Shape(CapabilityCatalog.Cover).ShouldBe("position, coverState | open, close, stop, setPosition(position)");
        Shape(CapabilityCatalog.AlarmPanel).ShouldBe("armState | arm(mode), disarm");
        Shape(CapabilityCatalog.MediaPlayer).ShouldBe(
            "power, volume, muted, source, app, playbackState | powerOn, powerOff, setVolume(volume), volumeUp, volumeDown, " +
            "setMuted(muted), selectSource(source), launchApp(app), play, pause, stop, next, previous");
        Shape(CapabilityCatalog.AirQuality).ShouldBe("pm25, co2, voc | ");
        Shape(CapabilityCatalog.Camera).ShouldBe("streamAvailable | snapshot");
        Shape(CapabilityCatalog.ApplianceCycle).ShouldBe("machineState, phase, remainingMinutes, remoteControlEnabled | start, pause, stop");
        Shape(CapabilityCatalog.Generic).ShouldBe(" | ");

        ((EnumSchema)CapabilityCatalog.Lock.FindAttribute("lockState")!.Schema).Values.ShouldBe(["locked", "unlocked", "jammed", "unknown"]);
        ((EnumSchema)CapabilityCatalog.Thermostat.FindAttribute("mode")!.Schema).Values.ShouldBe(["off", "cool", "heat", "auto", "dry", "fan"]);
        ((EnumSchema)CapabilityCatalog.Cover.FindAttribute("coverState")!.Schema).Values
            .ShouldBe(["open", "closed", "opening", "closing", "stopped", "unknown"]);
        ((EnumSchema)CapabilityCatalog.AlarmPanel.FindAttribute("armState")!.Schema).Values
            .ShouldBe(["disarmed", "armed_home", "armed_away", "armed_night", "triggered", "arming"]);
        ((EnumSchema)CapabilityCatalog.ApplianceCycle.FindAttribute("machineState")!.Schema).Values
            .ShouldBe(["idle", "running", "paused", "finished", "error"]);
    }

    [Fact]
    public void Energy_power_and_battery_have_the_planned_classes()
    {
        var power = CapabilityCatalog.Power.FindAttribute("power")!;
        power.Unit.ShouldBe(Units.Watt);
        power.StateClass.ShouldBe(StateClass.Measurement);
        power.Recorded.ShouldBeTrue();

        var energy = CapabilityCatalog.Energy.FindAttribute("energy")!;
        energy.Unit.ShouldBe(Units.WattHour);
        energy.StateClass.ShouldBe(StateClass.TotalIncreasing);
        energy.Recorded.ShouldBeTrue();

        var battery = CapabilityCatalog.Battery.FindAttribute("battery")!;
        battery.Unit.ShouldBe(Units.Percent);
        battery.Category.ShouldBe(AttributeCategory.Diagnostic);
    }

    [Fact]
    public void Find_setter_returns_the_single_argument_command_bound_to_an_attribute()
    {
        CapabilityCatalog.Level.FindSetter("level")!.Name.ShouldBe("setLevel");
        CapabilityCatalog.Thermostat.FindSetter("targetTemperature")!.Name.ShouldBe("setTargetTemperature");
        CapabilityCatalog.Switch.FindSetter("on").ShouldBeNull();
        CapabilityCatalog.Color.FindSetter("hue").ShouldBeNull(); // setColor needs hue and saturation
    }

    [Fact]
    public void Find_and_try_get_look_up_by_exact_id()
    {
        CapabilityCatalog.Find("switch").ShouldBeSameAs(CapabilityCatalog.Switch);
        CapabilityCatalog.Find("Switch").ShouldBeNull();
        CapabilityCatalog.TryGet("lock", out var definition).ShouldBeTrue();
        definition.ShouldBeSameAs(CapabilityCatalog.Lock);
        CapabilityCatalog.TryGet("nope", out _).ShouldBeFalse();
    }

    private static string Shape(CapabilityDefinition capability) =>
        string.Join(", ", capability.Attributes.Select(a => a.Key)) + " | " +
        string.Join(", ", capability.Commands.Select(c =>
            c.Arguments.Count == 0 ? c.Name : $"{c.Name}({string.Join(", ", c.Arguments.Select(a => a.Required ? a.Key : a.Key + "?"))})"));

    [GeneratedRegex("^[a-z][A-Za-z0-9]*$")]
    private static partial Regex CamelCase();
}

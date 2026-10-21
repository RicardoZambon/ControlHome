using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.SmartThings.Mapping;
using ControlHome.Connectors.SmartThings.Model;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;

namespace ControlHome.Connectors.Tests.SmartThings;

/// <summary>The pure device mapper against Home Assistant's SmartThings fixtures.</summary>
public sealed class DeviceMapperTests
{
    [Fact]
    public void A_color_bulb_becomes_switch_level_and_color_with_its_ranges()
    {
        var device = StFixtures.Map(StFixtures.Light);

        device.Device.Name.ShouldBe("Standing light");
        device.Device.DeviceClass.ShouldBe(DeviceClasses.Light);
        device.Device.Manufacturer.ShouldBe("Signify Netherlands B.V.");
        device.Device.Model.ShouldBe("Hue color lamp");
        device.Device.Firmware.ShouldBe("1.122.2");
        var main = device.Device.Endpoints.ShouldHaveSingleItem();
        main.Key.ShouldBe("main");
        main.Capabilities.Select(c => c.CapabilityId).ShouldBe([CapabilityIds.Switch, CapabilityIds.Level, CapabilityIds.Color, CapabilityIds.Generic], ignoreOrder: true);
        Constraint(main, CapabilityIds.Level, "level").ShouldBe(new AttributeConstraint(Min: 1, Max: 100));
        Constraint(main, CapabilityIds.Color, "colorTemperature").ShouldBe(new AttributeConstraint(Min: 2000, Max: 6535));

        Value(device, CapabilityIds.Switch, "on").ShouldBe(StateValue.False);
        Value(device, CapabilityIds.Level, "level").ShouldBe(StateValue.Number(70));
        Value(device, CapabilityIds.Color, "hue").ShouldBe(StateValue.Number(218.91)); // 60.8072 % of the circle
        Value(device, CapabilityIds.Color, "saturation").ShouldBe(StateValue.Number(60));
        Value(device, CapabilityIds.Color, "colorTemperature").ShouldBe(StateValue.Number(3000));
        device.Readings.Single(r => r.Attribute == "on").ObservedAtUtc.ShouldBe(DateTimeOffset.Parse("2025-02-08T07:08:19.519Z", System.Globalization.CultureInfo.InvariantCulture));

        // healthCheck / refresh are plumbing; the vendor's own capability stays generic (inferred: no definition).
        var generic = Generic(main);
        generic.Attributes.Select(a => a.Key).ShouldContain("hueSyncMode");
        generic.Attributes.ShouldNotContain(a => a.Key.Contains("deviceWatch", StringComparison.OrdinalIgnoreCase));
        generic.Commands.ShouldBeEmpty();
        device.Availability.ShouldBe(Availability.Online);
    }

    [Fact]
    public void An_air_conditioner_is_a_thermostat_whose_switch_is_the_off_mode_and_hides_disabled_parts()
    {
        var device = StFixtures.Map(StFixtures.Ac);

        device.Device.Name.ShouldBe("Office AC");
        device.Device.DeviceClass.ShouldBe(DeviceClasses.AirConditioner);
        device.Device.Manufacturer.ShouldBe("Samsung Electronics");
        device.Device.Firmware.ShouldBe("0.1.0");

        // Component "1" is listed in custom.disabledComponents.
        var main = device.Device.Endpoints.ShouldHaveSingleItem();
        main.Key.ShouldBe("main");
        var capabilities = main.Capabilities.Select(c => c.CapabilityId).ToList();
        capabilities.ShouldContain(CapabilityIds.Thermostat);
        capabilities.ShouldContain(CapabilityIds.Switch);
        capabilities.ShouldContain(CapabilityIds.Humidity);
        capabilities.ShouldContain(CapabilityIds.Energy);
        capabilities.ShouldContain(CapabilityIds.Power);

        // Disabled: remoteControlStatus, airQualitySensor, dustSensor, … — and the thermostat owns the temperature.
        capabilities.ShouldNotContain(CapabilityIds.AirQuality);
        capabilities.ShouldNotContain(CapabilityIds.Temperature);
        Generic(main).Attributes.ShouldNotContain(a => a.Key.StartsWith("dustFilter", StringComparison.Ordinal) || a.Key == "remoteControlEnabled");

        Constraint(main, CapabilityIds.Thermostat, "mode")!.Values.ShouldBe(["off", "cool", "heat", "auto", "dry", "fan"]);
        Constraint(main, CapabilityIds.Thermostat, "targetTemperature").ShouldBe(new AttributeConstraint(Min: 16, Max: 30));
        Constraint(main, CapabilityIds.Thermostat, "fanMode")!.Values.ShouldBe(["auto", "low", "medium", "high", "turbo"]);

        // The switch is off: the mode is "off", whatever airConditionerMode (heat) says.
        Value(device, CapabilityIds.Thermostat, "mode").ShouldBe(StateValue.String("off"));
        Value(device, CapabilityIds.Thermostat, "targetTemperature").ShouldBe(StateValue.Number(25));
        Value(device, CapabilityIds.Thermostat, "currentTemperature").ShouldBe(StateValue.Number(25));
        Value(device, CapabilityIds.Thermostat, "fanMode").ShouldBe(StateValue.String("low"));
        Value(device, CapabilityIds.Switch, "on").ShouldBe(StateValue.False);
        Value(device, CapabilityIds.Humidity, "humidity").ShouldBe(StateValue.Number(60));
        Value(device, CapabilityIds.Energy, "energy").ShouldBe(StateValue.Number(2_247_300));
        Value(device, CapabilityIds.Power, "power").ShouldBe(StateValue.Number(0));

        var plan = device.Endpoints["main"].Thermostat.ShouldNotBeNull();
        plan.ModeCapability.ShouldBe("airConditionerMode");
        plan.SwitchTurnsOff.ShouldBeTrue();
        plan.ModeToVendor["fan"].ShouldBe("wind");
        device.EnergyWindows.ShouldHaveSingleItem().DeltaWh.ShouldBe(400);
    }

    [Fact]
    public void A_washer_is_an_appliance_cycle_gated_by_remote_control_with_its_options_generic()
    {
        var device = StFixtures.Map(StFixtures.Washer, StFixtures.Definitions());

        device.Device.DeviceClass.ShouldBe(DeviceClasses.Washer);
        device.Device.Endpoints.Select(e => e.Key).ShouldBe(["main", "hca_main"]);
        var main = device.Device.Endpoints[0];
        main.Capabilities.Select(c => c.CapabilityId).ShouldContain(CapabilityIds.ApplianceCycle);
        main.Capabilities.Select(c => c.CapabilityId).ShouldContain(CapabilityIds.Switch);

        Value(device, CapabilityIds.ApplianceCycle, "machineState").ShouldBe(StateValue.String("idle"));
        Value(device, CapabilityIds.ApplianceCycle, "phase").ShouldBe(StateValue.Null);
        Value(device, CapabilityIds.ApplianceCycle, "remainingMinutes").ShouldBe(StateValue.Number(0));
        Value(device, CapabilityIds.ApplianceCycle, "remoteControlEnabled").ShouldBe(StateValue.False);
        device.Endpoints["main"].Appliance.ShouldBe(new AppliancePlan("washerOperatingState", SetMachineState: true));

        // From the capability definition: an enum with its setter (supported… lists are not attributes).
        var generic = Generic(main);
        var spin = generic.Attributes.Single(a => a.Key == "washerSpinLevel");
        spin.Schema.ShouldBe(new EnumSchema(["rinseHold", "noSpin", "low", "medium", "high", "extraHigh"]));
        spin.Writable.ShouldBeTrue();
        generic.FindSetter("washerSpinLevel")!.Name.ShouldBe("setWasherSpinLevel");
        generic.Attributes.ShouldNotContain(a => a.Key.Contains("supportedWasherSpinLevel", StringComparison.OrdinalIgnoreCase) || a.Key.Contains("SupportedCourses", StringComparison.Ordinal));
        Value(device, CapabilityIds.Generic, "washerSpinLevel").ShouldBe(StateValue.String("high"));
        generic.Attributes.Single(a => a.Key == "washerDelayEndRemainingTime").Schema.ShouldBe(new NumberSchema(0, 1440, 1, Units.Minute, Integer: true));

        // A capability that may lock or open something never gets a generic command.
        generic.Attributes.Single(a => a.Key == "kidsLockLockState").Writable.ShouldBeFalse();
        generic.Commands.ShouldNotContain(c => c.Name.Contains("lock", StringComparison.OrdinalIgnoreCase));

        // The hca.main component: its own endpoint with the washer mode.
        var hca = Generic(device.Device.Endpoints[1]);
        hca.FindSetter("washerMode")!.Name.ShouldBe("setMode");
        device.MissingDefinitions.ShouldBeEmpty();
    }

    [Fact]
    public void Undefined_capabilities_wait_for_their_definition_instead_of_being_guessed()
    {
        var device = StFixtures.Map(StFixtures.Washer, StFixtures.Definitions(otherwise: DefinitionLookup.Pending));

        device.MissingDefinitions.Select(c => c.Id).ShouldContain("custom.washerRinseCycles");
        device.MissingDefinitions.Select(c => c.Id).ShouldNotContain("custom.washerSpinLevel");
        device.MissingDefinitions.Select(c => c.Id).ShouldNotContain("washerOperatingState"); // a catalogue mapping
        device.MissingDefinitions.Select(c => c.Id).ShouldNotContain("ocf"); // plumbing
        Generic(device.Device.Endpoints[0]).Attributes.ShouldNotContain(a => a.Key.StartsWith("washerRinseCycles", StringComparison.Ordinal));
    }

    [Fact]
    public void A_fridge_has_an_endpoint_per_enabled_compartment_with_converted_temperatures()
    {
        var device = StFixtures.Map(StFixtures.Fridge, StFixtures.Definitions());

        device.Device.DeviceClass.ShouldBe("refrigerator");
        // icemaker-02, pantry-01, pantry-02, cvroom and onedoor are disabled.
        device.Device.Endpoints.Select(e => e.Key).ShouldBe(["main", "freezer", "cooler", "icemaker"], ignoreOrder: true);

        Value(device, CapabilityIds.Temperature, "temperature", "freezer").ShouldBe(StateValue.Number(-17.78)); // 0 °F
        Value(device, CapabilityIds.Temperature, "temperature", "cooler").ShouldBe(StateValue.Number(2.78)); // 37 °F
        Value(device, CapabilityIds.Contact, "open", "freezer").ShouldBe(StateValue.False);
        Value(device, CapabilityIds.Switch, "on", "icemaker").ShouldBe(StateValue.True);

        // The compartment setpoint: generic, writable, in °C with the device's range (−8…5 °F).
        var freezer = Generic(device.Device.Endpoints.Single(e => e.Key == "freezer"));
        var setpoint = freezer.Attributes.Single(a => a.Key == "coolingSetpoint");
        setpoint.Schema.ShouldBe(new NumberSchema(-22.22, -15, null, Units.Celsius));
        freezer.FindSetter("coolingSetpoint")!.Name.ShouldBe("setCoolingSetpoint");
        Value(device, CapabilityIds.Generic, "coolingSetpoint", "freezer").ShouldBe(StateValue.Number(-17.78));

        // main: its temperature and setpoint are disabled; the energy report feeds power and energy.
        var main = device.Device.Endpoints.Single(e => e.Key == "main");
        main.Capabilities.Select(c => c.CapabilityId).ShouldNotContain(CapabilityIds.Temperature);
        Value(device, CapabilityIds.Power, "power").ShouldBe(StateValue.Number(74));
        Value(device, CapabilityIds.Energy, "energy").ShouldBe(StateValue.Number(1_446_085));
        Value(device, CapabilityIds.Contact, "open").ShouldBe(StateValue.False);
        device.EnergyWindows.ShouldHaveSingleItem().Endpoint.ShouldBe("main");
    }

    [Fact]
    public void A_tv_is_a_media_player_with_its_input_sources()
    {
        var device = StFixtures.Map(StFixtures.Tv);

        device.Device.DeviceClass.ShouldBe(DeviceClasses.Tv);
        device.Device.Model.ShouldBe("UN49MU8000");
        var main = device.Device.Endpoints.ShouldHaveSingleItem();
        main.Capabilities.Select(c => c.CapabilityId).ShouldContain(CapabilityIds.MediaPlayer);
        main.Capabilities.Select(c => c.CapabilityId).ShouldNotContain(CapabilityIds.Switch); // the switch is the TV's power
        Constraint(main, CapabilityIds.MediaPlayer, "source")!.Values.ShouldBe(["digitalTv", "HDMI1", "HDMI2", "HDMI3"]);

        Value(device, CapabilityIds.MediaPlayer, "power").ShouldBe(StateValue.True);
        Value(device, CapabilityIds.MediaPlayer, "volume").ShouldBe(StateValue.Number(13));
        Value(device, CapabilityIds.MediaPlayer, "muted").ShouldBe(StateValue.True);
        Value(device, CapabilityIds.MediaPlayer, "source").ShouldBe(StateValue.String("HDMI1"));
        Value(device, CapabilityIds.MediaPlayer, "playbackState").ShouldBe(StateValue.Null);
        device.Endpoints["main"].Media.ShouldBe(new MediaPlan(Switch: true, Volume: true, Mute: true, "mediaInputSource", Playback: true, TrackControl: true, LaunchApp: true));

        // Disabled ambient capabilities are gone; the channel stays generic.
        var generic = Generic(main);
        generic.Attributes.Select(a => a.Key).ShouldContain("tvChannel");
        generic.Attributes.ShouldNotContain(a => a.Key.Contains("ambient", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_lock_maps_its_state_and_never_exposes_the_lock_codes()
    {
        var device = StFixtures.Map(StFixtures.Lock, StFixtures.Definitions());

        device.Device.DeviceClass.ShouldBe(DeviceClasses.Lock);
        var main = device.Device.Endpoints.ShouldHaveSingleItem();
        main.Capabilities.Select(c => c.CapabilityId).ShouldBe([CapabilityIds.Lock, CapabilityIds.Battery], ignoreOrder: true);
        Value(device, CapabilityIds.Lock, "lockState").ShouldBe(StateValue.String("locked"));
        Value(device, CapabilityIds.Battery, "battery").ShouldBe(StateValue.Number(86));
        device.Readings.ShouldNotContain(r => r.Value.S != null && r.Value.S.Contains("Code", StringComparison.Ordinal));
        device.Endpoints["main"].Lock.ShouldBeTrue();
    }

    [Fact]
    public void Sensors_map_to_their_catalogue_capabilities_in_canonical_units()
    {
        var door = StFixtures.Map(StFixtures.Multipurpose);
        door.Device.DeviceClass.ShouldBe(DeviceClasses.Door); // the user's category
        Value(door, CapabilityIds.Contact, "open").ShouldBe(StateValue.False);
        Value(door, CapabilityIds.Temperature, "temperature").ShouldBe(StateValue.Number(19.44)); // 67 °F
        Value(door, CapabilityIds.Battery, "battery").ShouldBe(StateValue.Number(50));
        Value(door, CapabilityIds.Generic, "acceleration").ShouldBe(StateValue.String("inactive"));

        var motion = StFixtures.Map(StFixtures.Motion);
        motion.Device.DeviceClass.ShouldBe(DeviceClasses.Sensor);
        Value(motion, CapabilityIds.Motion, "motion").ShouldBe(StateValue.False);
        Value(motion, CapabilityIds.Humidity, "humidity").ShouldBe(StateValue.Number(60));
        Value(motion, CapabilityIds.Temperature, "temperature").ShouldBe(StateValue.Number(22.2));
        Value(motion, CapabilityIds.Generic, "illuminance").ShouldBe(StateValue.Number(30));
        Generic(motion.Device.Endpoints[0]).Attributes.Single(a => a.Key == "illuminance").Unit.ShouldBe("lx");
        motion.Device.ParentExternalId.ShouldBeNull(); // its parent hub is not in this inventory
    }

    [Fact]
    public void An_offline_device_is_offline_and_unknown_values_stay_unknown()
    {
        var device = StFixtures.Map(StFixtures.OfflineThermostat);

        device.Availability.ShouldBe(Availability.Offline);
        device.Device.DeviceClass.ShouldBe(DeviceClasses.Thermostat);
        Value(device, CapabilityIds.Thermostat, "mode").ShouldBe(StateValue.Null);
        Value(device, CapabilityIds.Thermostat, "targetTemperature").ShouldBe(StateValue.Null);
        Value(device, CapabilityIds.Thermostat, "currentTemperature").ShouldBe(StateValue.Null);
        device.Endpoints["main"].Thermostat!.ModeToVendor.Keys.ShouldBe(["off", "cool", "heat", "auto"], ignoreOrder: true);
    }

    [Fact]
    public void Health_from_the_listing_wins_and_rooms_become_suggestions()
    {
        var item = StFixtures.ListingItem(StFixtures.Plug, "loc-1", health: "OFFLINE");
        var device = SmartThingsDevice.Parse(StFixtures.Parse(item.ToJsonString()))!;
        var rooms = new Dictionary<string, string> { [device.RoomId!] = "Living room" };

        var mapped = SmartThingsDeviceMapper.Map(device, DeviceStatus.FromListing(StFixtures.Parse(item.ToJsonString())), StFixtures.Context(rooms: rooms))!;

        mapped.Availability.ShouldBe(Availability.Offline);
        mapped.Device.SuggestedRoom.ShouldBe("Living room");
        mapped.Device.DeviceClass.ShouldBe(DeviceClasses.Switch);
        Value(mapped, CapabilityIds.Switch, "on").ShouldBe(StateValue.True);
    }

    [Fact]
    public void A_button_press_is_observed_with_its_timestamp()
    {
        var device = Build(
            """{"deviceId":"btn-1","label":"Button","components":[{"id":"main","capabilities":[{"id":"button","version":1},{"id":"battery","version":1}],"categories":[{"name":"Button","categoryType":"manufacturer"}]}]}""",
            """{"components":{"main":{"button":{"button":{"value":"double","timestamp":"2026-10-21T11:00:00Z"},"numberOfButtons":{"value":1}},"battery":{"battery":{"value":90,"unit":"%"}}}}}""");

        device.Buttons.ShouldHaveSingleItem().ShouldBe(new ButtonObservation("main", "double", DateTimeOffset.Parse("2026-10-21T11:00:00Z", System.Globalization.CultureInfo.InvariantCulture)));
        device.Device.Endpoints[0].Capabilities.Select(c => c.CapabilityId).ShouldBe([CapabilityIds.Button, CapabilityIds.Battery], ignoreOrder: true);
    }

    [Fact]
    public void Garage_doors_are_covers_of_class_garage_so_opening_is_safety_critical()
    {
        var device = Build(
            """{"deviceId":"gd-1","label":"Garage","components":[{"id":"main","capabilities":[{"id":"doorControl","version":1}],"categories":[{"name":"Door","categoryType":"manufacturer"}]}]}""",
            """{"components":{"main":{"doorControl":{"door":{"value":"closed"}}}}}""");

        device.Device.DeviceClass.ShouldBe(DeviceClasses.Garage);
        CapabilityCatalog.Cover.FindCommand("open")!.IsSafetyCriticalFor(device.Device.DeviceClass).ShouldBeTrue();
        Value(device, CapabilityIds.Cover, "coverState").ShouldBe(StateValue.String("closed"));
        device.Endpoints["main"].Cover.ShouldBe(new CoverPlan("doorControl", CanPause: false, null));
    }

    [Fact]
    public void Timestamps_from_the_future_are_clamped_to_now()
    {
        var device = Build(
            """{"deviceId":"p-1","components":[{"id":"main","capabilities":[{"id":"switch","version":1}],"categories":[]}]}""",
            """{"components":{"main":{"switch":{"switch":{"value":"on","timestamp":"2030-01-01T00:00:00Z"}}}}}""");

        device.Readings.ShouldHaveSingleItem().ObservedAtUtc.ShouldBe(StFixtures.Now);
    }

    [Theory]
    [InlineData("""{"deviceId":"x","components":"nope"}""", """{"components":[]}""")]
    [InlineData("""{"deviceId":"x","components":[{"id":"main","capabilities":[{"id":"switch","version":"one"},{"id":5},7]}]}""", """{"components":{"main":{"switch":{"switch":{"value":{"weird":true}}}}}}""")]
    [InlineData("""{"deviceId":"x","components":[{"id":"main","capabilities":[{"id":"switchLevel","version":1},{"id":"colorControl","version":1},{"id":"airConditionerMode","version":1}]}]}""", """{"components":{"main":{"switchLevel":{"level":{"value":"loud"},"levelRange":{"value":[1,2]}},"colorControl":{"hue":{"value":500}},"airConditionerMode":{"supportedAcModes":{"value":"cool"}}}}}""")]
    public void Malformed_vendor_payloads_degrade_instead_of_throwing(string description, string status)
    {
        var device = SmartThingsDevice.Parse(StFixtures.Parse(description));
        device.ShouldNotBeNull();

        var mapped = Should.NotThrow(() => SmartThingsDeviceMapper.Map(device, DeviceStatus.Parse(StFixtures.Parse(status)), StFixtures.Context()));

        (mapped?.Readings ?? []).Where(r => r.Capability != CapabilityIds.Generic).ShouldAllBe(r => r.Value == StateValue.Null);
    }

    [Fact]
    public void A_device_without_anything_usable_is_skipped()
    {
        var device = SmartThingsDevice.Parse(StFixtures.Parse("""{"deviceId":"hub","components":[{"id":"main","capabilities":[{"id":"bridge","version":1},{"id":"refresh","version":1}]}]}"""))!;

        SmartThingsDeviceMapper.Map(device, DeviceStatus.Empty, StFixtures.Context()).ShouldBeNull();
    }

    [Fact]
    public void Every_fixture_maps_to_a_device_the_inventory_accepts()
    {
        foreach (var name in (string[])[StFixtures.Light, StFixtures.Plug, StFixtures.Ac, StFixtures.Washer, StFixtures.Fridge, StFixtures.Tv, StFixtures.Lock, StFixtures.Multipurpose, StFixtures.Motion, StFixtures.OfflineThermostat])
        {
            var device = StFixtures.Map(name, StFixtures.Definitions()).Device;

            device.Endpoints.ShouldNotBeEmpty(name);
            device.Endpoints.Select(e => e.Key).ShouldBeUnique(name);
            foreach (var endpoint in device.Endpoints)
            {
                endpoint.Capabilities.Select(c => c.CapabilityId).ShouldBeUnique(name);
                endpoint.Capabilities.ShouldAllBe(c => CapabilityCatalog.Find(c.CapabilityId) != null, name);
                if (endpoint.Capabilities.FirstOrDefault(c => c.CapabilityId == CapabilityIds.Generic)?.GenericDefinition is { } generic)
                {
                    generic.Attributes.Select(a => a.Key).ShouldBeUnique(name);
                    generic.Commands.Select(c => c.Name).ShouldBeUnique(name);
                }

                foreach (var binding in endpoint.Capabilities.Where(c => c.Constraints is not null))
                {
                    CapabilityConstraints.TryParse(binding.Constraints, out _).ShouldBeTrue(name);
                }
            }

            // The JSON the host stores round-trips.
            CapabilityJson.Deserialize<DiscoveredDevice>(CapabilityJson.Serialize(device)).ShouldNotBeNull(name);
        }
    }

    private static MappedDevice Build(string description, string status) =>
        SmartThingsDeviceMapper.Map(SmartThingsDevice.Parse(StFixtures.Parse(description))!, DeviceStatus.Parse(StFixtures.Parse(status)), StFixtures.Context())!;

    private static StateValue? Value(MappedDevice device, string capability, string attribute, string endpoint = "main") =>
        device.Readings.LastOrDefault(r => r.Endpoint == endpoint && r.Capability == capability && r.Attribute == attribute)?.Value;

    private static AttributeConstraint? Constraint(DiscoveredEndpoint endpoint, string capability, string attribute) =>
        CapabilityConstraints.Parse(endpoint.Capabilities.Single(c => c.CapabilityId == capability).Constraints).For(attribute);

    private static CapabilityDefinition Generic(DiscoveredEndpoint endpoint) =>
        endpoint.Capabilities.Single(c => c.CapabilityId == CapabilityIds.Generic).GenericDefinition!;
}

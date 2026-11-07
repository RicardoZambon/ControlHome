using System.Text.Json;
using ControlHome.Connectors.DahuaHttp.Model;
using ControlHome.Connectors.DahuaHttp.Protocol;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;

namespace ControlHome.Connectors.Tests.DahuaHttp;

/// <summary>The inventory per device kind and the local event filter (pure).</summary>
public sealed class DahuaMappingTests
{
    private static readonly IReadOnlyDictionary<int, IReadOnlySet<string>> _rules =
        DahuaKeyValues.Parse(DahuaFixtures.Text("video-analyse-rule.txt")).EnabledRuleTypes();

    [Fact]
    public void A_camera_is_one_device_with_camera_motion_smart_rules_and_alarm_inputs()
    {
        var inventory = Camera(alarms: 1);

        var device = inventory.Devices.ShouldHaveSingleItem();
        (device.ExternalId, device.Name, device.DeviceClass, device.Manufacturer, device.Model, device.Firmware)
            .ShouldBe(("SYNTH00000001", "Entrada", DeviceClasses.Camera, "Intelbras", "VIP 1230 B G4", "2.800.00IB000.0.R"));
        device.Connections.ShouldBe(new Dictionary<string, string> { ["serial"] = "SYNTH00000001" });
        device.Endpoints.Select(e => e.Key).ShouldBe(["main", "tripwire", "alarm1"]);
        device.Endpoints[0].Capabilities.Select(c => c.CapabilityId).ShouldBe([CapabilityIds.Camera, CapabilityIds.Motion]);
        device.Endpoints[1].Name.ShouldBe("Linha virtual");
        device.Endpoints[2].Name.ShouldBe("Entrada de alarme 1");
        device.Endpoints[2].Capabilities.ShouldHaveSingleItem().CapabilityId.ShouldBe(CapabilityIds.Contact);
        inventory.ChannelOf("SYNTH00000001", "main").ShouldBe(1);
        inventory.ChannelOf("SYNTH00000001", "tripwire").ShouldBeNull();
        inventory.CameraForIndex(0).ShouldBe("SYNTH00000001");
        inventory.CameraForIndex(1).ShouldBeNull();
    }

    [Fact]
    public void A_recorder_has_one_camera_child_per_selected_channel()
    {
        var titles = DahuaKeyValues.Parse(DahuaFixtures.Text("channel-title-recorder.txt")).ChannelTitles();
        var inventory = DahuaInventory.Create(new DahuaInventoryInput(
            DahuaDeviceKind.Recorder,
            new DahuaIdentity("NVD 1404", "4.001.00IB000.0", "SYNTHNVR0001", null, null),
            "fallback",
            [.. titles.Where(t => t.Key != 2).Select(t => (t.Key, (string?)t.Value))],
            0,
            _rules,
            "en"));

        inventory.Devices.Select(d => (d.ExternalId, d.Name, d.ParentExternalId, d.DeviceClass)).ShouldBe(
        [
            ("SYNTHNVR0001", "NVD 1404", (string?)null, DeviceClasses.Nvr),
            ("SYNTHNVR0001:ch1", "Garagem", "SYNTHNVR0001", DeviceClasses.Camera),
            ("SYNTHNVR0001:ch3", "Quintal", "SYNTHNVR0001", DeviceClasses.Camera),
            ("SYNTHNVR0001:ch4", "Camera 4", "SYNTHNVR0001", DeviceClasses.Camera),
        ]);
        var root = inventory.Devices[0].Endpoints.ShouldHaveSingleItem();
        root.Capabilities.ShouldHaveSingleItem().GenericDefinition!.Attributes.ShouldHaveSingleItem().Key.ShouldBe("channels");
        inventory.Devices[1].Endpoints.Select(e => e.Key).ShouldBe(["main", "tripwire"]);
        inventory.Devices[2].Endpoints.Select(e => e.Key).ShouldBe(["main", "intrusion"]);
        inventory.CameraForIndex(2).ShouldBe("SYNTHNVR0001:ch3");
        inventory.CameraForIndex(1).ShouldBeNull("channel 2 was not selected");
        inventory.ImportedChannels.ShouldBe(3);
    }

    [Fact]
    public void An_access_terminal_has_a_door_with_opener_contact_and_access_events()
    {
        var inventory = Access();

        var door = inventory.Devices.ShouldHaveSingleItem();
        door.DeviceClass.ShouldBe(DeviceClasses.Door);
        var main = door.Endpoints.ShouldHaveSingleItem();
        main.Capabilities.Select(c => c.CapabilityId).ShouldBe([CapabilityIds.DoorOpener, CapabilityIds.Contact, CapabilityIds.Generic]);
        main.Capabilities[2].GenericDefinition!.Attributes.ShouldHaveSingleItem().Schema.ShouldBeOfType<MomentarySchema>().Events
            .ShouldBe(["granted", "denied", "unknown"]);
        inventory.DoorOf("SYNTH00000002", "main").ShouldBe(1);
        inventory.ChannelOf("SYNTH00000002", "main").ShouldBeNull();
    }

    [Fact]
    public void A_device_without_serial_uses_the_fallback_id()
    {
        var inventory = DahuaInventory.Create(new DahuaInventoryInput(
            DahuaDeviceKind.Camera,
            new DahuaIdentity("VIP 1230 B G4", null, " ", null, "Dahua"),
            "dahua-abc",
            [],
            0,
            new Dictionary<int, IReadOnlySet<string>>(),
            "en"));

        var device = inventory.Devices.ShouldHaveSingleItem();
        (device.ExternalId, device.Manufacturer, device.Name).ShouldBe(("dahua-abc", "Dahua", "VIP 1230 B G4"));
        device.Connections.ShouldBeEmpty();
    }

    [Fact]
    public void Endpoints_announced_by_events_are_added_once()
    {
        var inventory = Camera(alarms: 0);

        var updated = inventory.EnsureEndpoint("SYNTH00000001", "intrusion").ShouldNotBeNull();
        updated.Endpoints.Select(e => e.Key).ShouldBe(["main", "tripwire", "intrusion"]);
        inventory.EnsureEndpoint("SYNTH00000001", "intrusion").ShouldBeNull();
        inventory.EnsureEndpoint("SYNTH00000001", "alarm3").ShouldNotBeNull().Endpoints.Select(e => e.Key).ShouldContain("alarm3");
        inventory.EnsureEndpoint("SYNTH00000001", "alarm99").ShouldBeNull();
        inventory.EnsureEndpoint("SYNTH00000001", "other").ShouldBeNull();
        inventory.EnsureEndpoint("unknown", "intrusion").ShouldBeNull();
    }

    [Fact]
    public void Motion_events_map_to_the_camera_and_its_smart_rule_endpoints()
    {
        var inventory = Camera(alarms: 0);

        Map("Code=VideoMotion;action=Start;index=0", inventory).ShouldBe([State("SYNTH00000001", "main", CapabilityIds.Motion, "motion", StateValue.True)]);
        Map("Code=VideoMotion;action=Stop;index=0", inventory).ShouldBe([State("SYNTH00000001", "main", CapabilityIds.Motion, "motion", StateValue.False)]);
        Map("Code=VideoMotion;action=Pulse;index=0", inventory).ShouldBe([State("SYNTH00000001", "main", CapabilityIds.Motion, "motion", StateValue.True, pulse: true)]);
        Map("Code=CrossLineDetection;action=Start;index=0", inventory).ShouldBe([State("SYNTH00000001", "tripwire", CapabilityIds.Motion, "motion", StateValue.True, pulse: true)]);
        Map("Code=CrossLineDetection;action=Stop;index=0", inventory).ShouldBe([State("SYNTH00000001", "tripwire", CapabilityIds.Motion, "motion", StateValue.False)]);
        Map("Code=CrossRegionDetection;action=Start;index=0", inventory).ShouldBe([State("SYNTH00000001", "intrusion", CapabilityIds.Motion, "motion", StateValue.True)]);
        Map("Code=VideoMotion;action=Start;index=5", inventory).ShouldBeEmpty("no such channel");
    }

    [Fact]
    public void Alarm_inputs_map_to_contacts_on_the_root_device()
    {
        var inventory = Camera(alarms: 2);

        Map("Code=AlarmLocal;action=Start;index=1", inventory).ShouldBe([State("SYNTH00000001", "alarm2", CapabilityIds.Contact, "open", StateValue.True)]);
        Map("Code=AlarmLocal;action=Stop;index=1", inventory).ShouldBe([State("SYNTH00000001", "alarm2", CapabilityIds.Contact, "open", StateValue.False)]);
        Map("Code=AlarmLocal;action=Pulse;index=1", inventory).ShouldBeEmpty();
    }

    [Fact]
    public void Video_loss_takes_a_recorder_channel_offline()
    {
        var inventory = DahuaInventory.Create(new DahuaInventoryInput(
            DahuaDeviceKind.Recorder,
            new DahuaIdentity("NVD 1404", null, "SYNTHNVR0001", null, null),
            "fallback",
            [(1, "A"), (2, "B")],
            0,
            new Dictionary<int, IReadOnlySet<string>>(),
            "en"));

        Map("Code=VideoLoss;action=Start;index=1", inventory).ShouldHaveSingleItem().ShouldBeOfType<DahuaAvailabilityEffect>()
            .ShouldSatisfyAllConditions(e => e.DeviceId.ShouldBe("SYNTHNVR0001:ch2"), e => e.Availability.ShouldBe(Availability.Offline));
        Map("Code=VideoLoss;action=Stop;index=1", inventory).ShouldHaveSingleItem().ShouldBeOfType<DahuaAvailabilityEffect>()
            .Availability.ShouldBe(Availability.Online);
        Map("Code=VideoLoss;action=Start;index=0", Camera(alarms: 0)).ShouldBeEmpty("a camera's own video loss is not a channel");
    }

    [Fact]
    public void Access_events_keep_only_allowlisted_data()
    {
        var parts = new MultipartEventStreamParser(DahuaFixtures.Boundary).Feed(DahuaFixtures.Multipart("event-stream-access.txt"));
        var events = parts.SelectMany(DahuaEventParser.Parse).ToList();
        var inventory = Access();

        var granted = Map(events[0], inventory).ShouldHaveSingleItem().ShouldBeOfType<DahuaMomentaryEffect>();
        (granted.DeviceId, granted.EndpointKey, granted.CapabilityId, granted.AttributeKey, granted.Event)
            .ShouldBe(("SYNTH00000002", "main", CapabilityIds.Generic, "access", "granted"));
        var data = granted.Data!.Value.GetRawText();
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(data)!.Keys.ShouldBe(["status", "method", "type", "userId", "errorCode", "door"], ignoreOrder: true);
        data.ShouldNotContain("00000000");
        data.ShouldNotContain("SnapPath", Case.Insensitive);
        data.ShouldNotContain("OpenDoor");

        Map(events[1], inventory).ShouldBe([State("SYNTH00000002", "main", CapabilityIds.Contact, "open", StateValue.True)]);
        Map(events[2], inventory).ShouldBe([State("SYNTH00000002", "main", CapabilityIds.Contact, "open", StateValue.False)]);
        Map(events[3], inventory).ShouldHaveSingleItem().ShouldBeOfType<DahuaMomentaryEffect>().Event.ShouldBe("denied");
        Map("Code=AccessControl;action=Pulse;index=0", inventory).ShouldHaveSingleItem().ShouldBeOfType<DahuaMomentaryEffect>().Event.ShouldBe("unknown");
        Map("Code=AccessControl;action=Pulse;index=1", inventory).ShouldBeEmpty("only door 1 is supported");
        Map("Code=AccessControl;action=Pulse;index=0", Camera(alarms: 0)).ShouldBeEmpty("a camera has no door");
    }

    [Theory]
    [InlineData("Code=NewFile;action=Pulse;index=0")]
    [InlineData("Code=VideoBlind;action=Start;index=0")]
    [InlineData("Code=TimeChange;action=Pulse;index=0")]
    public void Unmapped_codes_yield_nothing(string text) => Map(text, Camera(alarms: 1)).ShouldBeEmpty();

    private static DahuaInventory Camera(int alarms) => DahuaInventory.Create(new DahuaInventoryInput(
        DahuaDeviceKind.Camera,
        new DahuaIdentity("VIP 1230 B G4", "2.800.00IB000.0.R", "SYNTH00000001", "Entrada", null),
        "fallback",
        [],
        alarms,
        _rules,
        "pt-BR"));

    private static DahuaInventory Access() => DahuaInventory.Create(new DahuaInventoryInput(
        DahuaDeviceKind.AccessControl,
        new DahuaIdentity("SS 3530 MF FACE", "2.000.00IB005.0.R", "SYNTH00000002", "Portaria", null),
        "fallback",
        [],
        0,
        new Dictionary<int, IReadOnlySet<string>>(),
        "en"));

    private static IReadOnlyList<DahuaEffect> Map(string text, DahuaInventory inventory) =>
        Map(DahuaEventParser.Parse(text).ShouldHaveSingleItem(), inventory);

    private static IReadOnlyList<DahuaEffect> Map(DahuaEvent e, DahuaInventory inventory) => DahuaEventMapper.Map(e, inventory);

    private static DahuaStateEffect State(string device, string endpoint, string capability, string attribute, StateValue value, bool pulse = false) =>
        new(device, endpoint, capability, attribute, value, pulse);
}

using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.Simulated;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;

namespace ControlHome.Connectors.Tests.Simulated;

/// <summary>The <c>demo</c> preset: about 20 devices covering multi-endpoint, child devices, generic, sleepy and safety cases.</summary>
public sealed class SimulatedDemoTests
{
    private const string Demo = """{"preset":"demo"}""";
    private const string Washer = "washer-1";
    private const string Tv = "tv-1";
    private const string Switch3 = "switch3-1";
    private const string Gate = "gate-1";
    private const string Tank = "tank-1";
    private const string Motion = "motion-1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_demo_home_is_the_basic_five_plus_fifteen_devices()
    {
        await using var sim = await SimulatedHarness.StartAsync(Demo);
        var devices = sim.Context.Devices;

        sim.Context.Syncs.ShouldHaveSingleItem().Count.ShouldBe(20);
        devices.Select(d => d.ExternalId).ShouldBe(
        [
            "light-1", "plug-1", "ac-1", "contact-1", "lock-1",
            "light-2", Washer, Tv, Motion, Switch3, "nvr-1", "cam-1", "cam-2", "cam-3", "cam-4", Gate, "blinds-1", Tank,
            "climate-1", "plug-2",
        ]);
        devices.Select(d => d.Connections["serial"]).Distinct().Count().ShouldBe(20);
        devices.Select(d => d.Connections["mac"]).Distinct().Count().ShouldBe(20);
        devices.Where(d => d.ParentExternalId == "nvr-1").Select(d => d.ExternalId).ShouldBe(["cam-1", "cam-2", "cam-3", "cam-4"]);
        devices.Where(d => d.ExternalId != "nvr-1" && !d.ExternalId.StartsWith("cam-", StringComparison.Ordinal))
            .ShouldAllBe(d => d.ParentExternalId == null);

        var switch3 = devices.Single(d => d.ExternalId == Switch3);
        switch3.Endpoints.Select(e => (e.Key, e.Name)).ShouldBe([("l1", "Ceiling"), ("l2", "Wall lights"), ("l3", "Porch")]);
        switch3.Endpoints.ShouldAllBe(e => e.Capabilities.Select(c => c.CapabilityId).SequenceEqual(new[] { "switch", "power" }));

        Capabilities(devices, Washer).ShouldBe(["applianceCycle", "power"]);
        Capabilities(devices, Tv).ShouldBe(["mediaPlayer"]);
        Capabilities(devices, Motion).ShouldBe(["motion", "battery"]);
        Capabilities(devices, Gate).ShouldBe(["cover", "contact"]);
        Capabilities(devices, "light-2").ShouldBe(["switch", "level", "color"]);
        Capabilities(devices, "cam-1").ShouldBe(["camera", "motion"]);
        devices.Single(d => d.ExternalId == Gate).DeviceClass.ShouldBe(DeviceClasses.Gate);

        var tank = devices.Single(d => d.ExternalId == Tank).Endpoints.Single().Capabilities.ShouldHaveSingleItem();
        tank.CapabilityId.ShouldBe(CapabilityIds.Generic);
        tank.GenericDefinition.ShouldNotBeNull().FindSetter("pump").ShouldNotBeNull().Name.ShouldBe("setPump");
        devices.Single(d => d.ExternalId == "nvr-1").Endpoints.Single().Capabilities.Single().GenericDefinition.ShouldNotBeNull();
    }

    [Fact]
    public async Task Every_demo_report_fits_its_definition_and_the_device_constraints()
    {
        await using var sim = await SimulatedHarness.StartAsync(Demo);
        await sim.AdvanceTicksAsync(40);
        await sim.ExecuteAsync("light-2", "color", "setColor", """{"hue":120,"saturation":60}""");
        await sim.ExecuteAsync(Tank, "generic", "setPump", """{"pump":true}""");
        await sim.ExecuteAsync(Gate, "cover", "open", key: "gate-open");
        await sim.AdvanceTicksAsync(5);

        var bindings = sim.Context.Devices.ToDictionary(
            d => d.ExternalId,
            d => d.Endpoints.ToDictionary(e => e.Key, e => e.Capabilities.ToDictionary(c => c.CapabilityId)));
        foreach (var report in sim.Context.States)
        {
            var binding = bindings[report.ExternalDeviceId][report.EndpointKey].GetValueOrDefault(report.CapabilityId);
            binding.ShouldNotBeNull($"{report.ExternalDeviceId}/{report.EndpointKey} has no {report.CapabilityId}");
            var definition = binding.GenericDefinition ?? CapabilityCatalog.Find(report.CapabilityId)!;
            var attribute = definition.FindAttribute(report.AttributeKey);
            attribute.ShouldNotBeNull($"{report.CapabilityId}.{report.AttributeKey}");
            if (report.Value.Kind == StateValueKind.Null)
            {
                continue;
            }

            var constraint = CapabilityConstraints.Parse(binding.Constraints).For(report.AttributeKey);
            ValueSchemaValidator.Accepts(attribute.Schema, report.Value, constraint)
                .ShouldBeTrue($"{report.ExternalDeviceId}/{report.CapabilityId}.{report.AttributeKey} = {report.Value}");
        }
    }

    [Fact]
    public async Task The_three_gang_channels_switch_independently_with_their_own_power()
    {
        await using var sim = await SimulatedHarness.StartAsync(Demo);
        Latest(sim, Switch3, "l2", "switch", "on").ShouldBe(StateValue.False);
        Latest(sim, Switch3, "l2", "power", "power").ShouldBe(StateValue.Number(0));

        (await sim.ExecuteOnAsync(Switch3, "l2", "switch", "turnOn")).ShouldBe(CommandOutcome.CompletedOutcome);
        await sim.ExecuteOnAsync(Switch3, "l1", "switch", "turnOff");
        await sim.AdvanceTicksAsync(1);

        Latest(sim, Switch3, "l2", "switch", "on").ShouldBe(StateValue.True);
        Latest(sim, Switch3, "l2", "power", "power").N!.Value.ShouldBeInRange(23.5, 24.5);
        Latest(sim, Switch3, "l1", "power", "power").ShouldBe(StateValue.Number(0));
        Latest(sim, Switch3, "l3", "power", "power").N!.Value.ShouldBeInRange(14.5, 15.5);
        (await sim.ExecuteOnAsync(Switch3, "l9", "switch", "turnOn")).ShouldBeOfType<CommandOutcome.Rejected>().Code
            .ShouldBe(CommandRejectionCodes.UnknownEndpoint);
    }

    [Fact]
    public async Task The_washer_counts_down_its_cycle_and_obeys_start_pause_stop()
    {
        await using var sim = await SimulatedHarness.StartAsync(Demo);
        Latest(sim, Washer, "main", "applianceCycle", "machineState").ShouldBe(StateValue.String("running"));
        Latest(sim, Washer, "main", "applianceCycle", "remainingMinutes").ShouldBe(StateValue.Number(42));
        Latest(sim, Washer, "main", "applianceCycle", "phase").ShouldBe(StateValue.String("wash"));
        Latest(sim, Washer, "main", "applianceCycle", "remoteControlEnabled").ShouldBe(StateValue.True);

        await sim.AdvanceTicksAsync(12); // one minute
        Latest(sim, Washer, "main", "applianceCycle", "remainingMinutes").ShouldBe(StateValue.Number(41));
        Latest(sim, Washer, "main", "power", "power").N!.Value.ShouldBeInRange(405, 495);

        await sim.ExecuteAsync(Washer, "applianceCycle", "pause");
        Latest(sim, Washer, "main", "applianceCycle", "machineState").ShouldBe(StateValue.String("paused"));
        await sim.AdvanceTicksAsync(24);
        Latest(sim, Washer, "main", "applianceCycle", "remainingMinutes").ShouldBe(StateValue.Number(41), "paused: no countdown");
        Latest(sim, Washer, "main", "power", "power").ShouldBe(StateValue.Number(1.5));

        await sim.ExecuteAsync(Washer, "applianceCycle", "stop");
        Latest(sim, Washer, "main", "applianceCycle", "machineState").ShouldBe(StateValue.String("idle"));
        Latest(sim, Washer, "main", "applianceCycle", "phase").ShouldBe(StateValue.Null);

        await sim.ExecuteAsync(Washer, "applianceCycle", "start");
        Latest(sim, Washer, "main", "applianceCycle", "machineState").ShouldBe(StateValue.String("running"));
        Latest(sim, Washer, "main", "applianceCycle", "remainingMinutes").ShouldBe(StateValue.Number(58));
    }

    [Fact]
    public async Task The_gate_opens_only_at_most_once_per_key_and_moves_over_ticks_with_its_contact()
    {
        await using var sim = await SimulatedHarness.StartAsync(Demo);
        Latest(sim, Gate, "main", "cover", "coverState").ShouldBe(StateValue.String("closed"));
        Latest(sim, Gate, "main", "contact", "open").ShouldBe(StateValue.False);

        (await sim.ExecuteAsync(Gate, "cover", "open", key: "")).ShouldBeOfType<CommandOutcome.Rejected>().Code
            .ShouldBe(CommandRejectionCodes.IdempotencyRequired);
        (await sim.ExecuteAsync(Gate, "cover", "setPosition", """{"position":50}""", key: "")).ShouldBeOfType<CommandOutcome.Rejected>().Code
            .ShouldBe(CommandRejectionCodes.IdempotencyRequired);
        (await sim.ExecuteAsync(Gate, "cover", "open", key: "open-1")).ShouldBe(CommandOutcome.CompletedOutcome);
        Latest(sim, Gate, "main", "cover", "coverState").ShouldBe(StateValue.String("opening"));
        (await sim.ExecuteAsync(Gate, "cover", "open", key: "open-1")).ShouldBeOfType<CommandOutcome.Rejected>().Code
            .ShouldBe(CommandRejectionCodes.Duplicate);

        await sim.AdvanceTicksAsync(1);
        Latest(sim, Gate, "main", "cover", "position").ShouldBe(StateValue.Number(25));
        Latest(sim, Gate, "main", "contact", "open").ShouldBe(StateValue.True);
        await sim.AdvanceTicksAsync(3);
        Latest(sim, Gate, "main", "cover", "coverState").ShouldBe(StateValue.String("open"));
        Latest(sim, Gate, "main", "cover", "position").ShouldBe(StateValue.Number(100));

        // Closing is the fail-safe direction: no key needed.
        (await sim.ExecuteAsync(Gate, "cover", "close", key: "")).ShouldBe(CommandOutcome.CompletedOutcome);
        Latest(sim, Gate, "main", "cover", "coverState").ShouldBe(StateValue.String("closing"));
        await sim.AdvanceTicksAsync(2);
        await sim.ExecuteAsync(Gate, "cover", "stop", key: "");
        Latest(sim, Gate, "main", "cover", "coverState").ShouldBe(StateValue.String("stopped"));
        await sim.ExecuteAsync(Gate, "cover", "close", key: "");
        await sim.AdvanceTicksAsync(2);
        Latest(sim, Gate, "main", "cover", "coverState").ShouldBe(StateValue.String("closed"));
        Latest(sim, Gate, "main", "contact", "open").ShouldBe(StateValue.False);
    }

    [Fact]
    public async Task The_tv_follows_media_commands()
    {
        await using var sim = await SimulatedHarness.StartAsync(Demo);

        await sim.ExecuteAsync(Tv, "mediaPlayer", "volumeUp");
        await sim.ExecuteAsync(Tv, "mediaPlayer", "setMuted", """{"muted":true}""");
        await sim.ExecuteAsync(Tv, "mediaPlayer", "launchApp", """{"app":"YouTube"}""");
        await sim.ExecuteAsync(Tv, "mediaPlayer", "pause");

        Latest(sim, Tv, "main", "mediaPlayer", "volume").ShouldBe(StateValue.Number(19));
        Latest(sim, Tv, "main", "mediaPlayer", "muted").ShouldBe(StateValue.True);
        Latest(sim, Tv, "main", "mediaPlayer", "app").ShouldBe(StateValue.String("YouTube"));
        Latest(sim, Tv, "main", "mediaPlayer", "playbackState").ShouldBe(StateValue.String("paused"));

        await sim.ExecuteAsync(Tv, "mediaPlayer", "powerOff");
        Latest(sim, Tv, "main", "mediaPlayer", "power").ShouldBe(StateValue.False);
        Latest(sim, Tv, "main", "mediaPlayer", "playbackState").ShouldBe(StateValue.String("idle"));
    }

    [Fact]
    public async Task The_generic_water_tank_runs_its_pump_through_its_own_setter()
    {
        await using var sim = await SimulatedHarness.StartAsync(Demo);
        var level = Latest(sim, Tank, "main", "generic", "level").N!.Value;

        (await sim.ExecuteAsync(Tank, "generic", "setPump", """{"pump":"yes"}""")).ShouldBeOfType<CommandOutcome.Rejected>().Code
            .ShouldBe(CommandRejectionCodes.InvalidArguments);
        (await sim.ExecuteAsync(Tank, "generic", "drain")).ShouldBeOfType<CommandOutcome.Rejected>().Code
            .ShouldBe(CommandRejectionCodes.UnsupportedCommand);
        (await sim.ExecuteAsync(Tank, "generic", "setPump", """{"pump":true}""")).ShouldBe(CommandOutcome.CompletedOutcome);
        await sim.AdvanceTicksAsync(4);

        Latest(sim, Tank, "main", "generic", "pump").ShouldBe(StateValue.True);
        Latest(sim, Tank, "main", "generic", "level").N!.Value.ShouldBe(level + 2, tolerance: 0.11);
    }

    [Fact]
    public async Task The_sleepy_motion_sensor_reports_only_when_it_wakes()
    {
        await using var sim = await SimulatedHarness.StartAsync(Demo);
        var initial = sim.Context.States.Count(s => s.ExternalDeviceId == Motion);

        await sim.AdvanceTicksAsync(360); // half an hour

        var reports = sim.Context.States.Where(s => s.ExternalDeviceId == Motion).Skip(initial).ToList();
        reports.Count.ShouldBeLessThan(150, "a sleepy sensor reports only on wake-ups (about one tick in 30)");
        var motions = reports.Where(r => r.CapabilityId == "motion").ToList();
        motions.ShouldNotBeEmpty();
        foreach (var on in motions.Where(r => r.Value == StateValue.True))
        {
            var off = motions.FirstOrDefault(r => r.ObservedAtUtc > on.ObservedAtUtc && r.Value == StateValue.False);
            if (off is not null)
            {
                (off.ObservedAtUtc!.Value - on.ObservedAtUtc!.Value).ShouldBe(TimeSpan.FromSeconds(30));
            }
        }
    }

    [Fact]
    public async Task Cameras_hand_out_their_bundled_snapshot()
    {
        await using var sim = await SimulatedHarness.StartAsync(Demo);
        var source = sim.Runtime.ShouldBeAssignableTo<ICameraSnapshotSource>();

        var before = await source.GetSnapshotAsync("cam-2", "main", Ct);
        (await sim.ExecuteAsync("cam-2", "camera", "snapshot")).ShouldBe(CommandOutcome.CompletedOutcome);
        var after = await source.GetSnapshotAsync("cam-2", "main", Ct);

        before.ShouldNotBeNull().ContentType.ShouldBe("image/png");
        before.Data.Span[..8].ToArray().ShouldBe(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        before.Data.Length.ShouldBeGreaterThan(1000);
        after.ShouldNotBeNull().TakenAtUtc.ShouldBe(SimulatedHarness.Start + SimulatedRuntime.CommandLatency);
        (await source.GetSnapshotAsync("cam-1", "main", Ct))!.Data.ToArray().ShouldNotBe(before.Data.ToArray());
        (await source.GetSnapshotAsync("light-1", "main", Ct)).ShouldBeNull();
        (await source.GetSnapshotAsync("cam-1", "other", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Cameras_stream_the_configured_test_source_only()
    {
        await using (var none = await SimulatedHarness.StartAsync(Demo))
        {
            (await ((ICameraStreamSource)none.Runtime).GetStreamSourceAsync("cam-1", "main", CameraStreamQuality.Main, Ct)).ShouldBeNull("no test stream configured");
        }

        await using var sim = await SimulatedHarness.StartAsync(Demo, new SimulatedConnectorOptions { CameraStreamSource = "rtsp://192.0.2.50:8554/demo{n}_{quality}" });
        var source = (ICameraStreamSource)sim.Runtime;

        (await source.GetStreamSourceAsync("cam-3", "main", CameraStreamQuality.Sub, Ct)).ShouldNotBeNull().Url.ShouldBe("rtsp://192.0.2.50:8554/demo3_sub");
        (await source.GetStreamSourceAsync("cam-1", "main", CameraStreamQuality.Main, Ct)).ShouldNotBeNull().Url.ShouldBe("rtsp://192.0.2.50:8554/demo1_main");
        (await source.GetStreamSourceAsync("light-1", "main", CameraStreamQuality.Main, Ct)).ShouldBeNull();
        (await source.GetStreamSourceAsync("cam-1", "other", CameraStreamQuality.Main, Ct)).ShouldBeNull();
        await using var bad = await SimulatedHarness.StartAsync(Demo, new SimulatedConnectorOptions { CameraStreamSource = "exec:ffmpeg -i x" });
        (await ((ICameraStreamSource)bad.Runtime).GetStreamSourceAsync("cam-1", "main", CameraStreamQuality.Main, Ct)).ShouldBeNull("only rtsp sources");
    }

    [Fact]
    public async Task The_same_seed_gives_the_same_demo_reports()
    {
        var first = await ReportsAfterAsync("""{"preset":"demo","seed":11}""", ticks: 20);
        var second = await ReportsAfterAsync("""{"preset":"demo","seed":11}""", ticks: 20);

        second.ShouldBe(first);
    }

    [Fact]
    public async Task The_static_inventory_is_what_the_runtime_reports()
    {
        await using var sim = await SimulatedHarness.StartAsync(Demo);

        var described = new SimulatedConnectorFactory().DescribeDevices(SimulatedHarness.Json(Demo));

        described.Select(Shape).ShouldBe(sim.Context.Devices.Select(Shape));
        new SimulatedConnectorFactory().DescribeDevices(SimulatedHarness.Json("{}")).Count.ShouldBe(5);
        Should.Throw<ArgumentException>(() => new SimulatedConnectorFactory().DescribeDevices(SimulatedHarness.Json("""{"preset":"x"}""")));

        static string Shape(DiscoveredDevice d) =>
            $"{d.ExternalId}|{d.Name}|{d.DeviceClass}|{d.ParentExternalId}|{string.Join(',', d.Connections.Values)}|" +
            string.Join(';', d.Endpoints.Select(e => $"{e.Key}:{string.Join(',', e.Capabilities.Select(c => c.CapabilityId))}"));
    }

    [Fact]
    public async Task Simulated_expired_credentials_need_reauthorization_and_report_nothing()
    {
        await using var sim = await SimulatedHarness.StartAsync("""{"preset":"demo","simulateReauth":true}""");

        sim.Context.Statuses.ShouldBe(
        [
            (IntegrationStatus.Connecting, (string?)null),
            (IntegrationStatus.NeedsReauth, SimulatedRuntime.ReauthMessage),
        ]);
        sim.Context.Devices.ShouldBeEmpty();
        sim.Context.States.ShouldBeEmpty();
        (await sim.ExecuteAsync("light-1", "switch", "turnOn")).ShouldBeOfType<CommandOutcome.Rejected>().Code
            .ShouldBe(CommandRejectionCodes.Unavailable);
        (await ((ICameraSnapshotSource)sim.Runtime).GetSnapshotAsync("cam-1", "main", Ct)).ShouldBeNull();
        sim.Runtime.Settings.SimulateReauth.ShouldBeTrue();
    }

    private static List<string> Capabilities(IReadOnlyList<DiscoveredDevice> devices, string device) =>
        devices.Single(d => d.ExternalId == device).Endpoints.Single().Capabilities.Select(c => c.CapabilityId).ToList();

    private static StateValue Latest(SimulatedHarness sim, string device, string endpoint, string capability, string attribute) =>
        sim.Context.States.Last(s => s.ExternalDeviceId == device && s.EndpointKey == endpoint && s.CapabilityId == capability && s.AttributeKey == attribute).Value;

    private static async Task<List<StateReport>> ReportsAfterAsync(string config, int ticks)
    {
        await using var sim = await SimulatedHarness.StartAsync(config);
        await sim.AdvanceTicksAsync(ticks);
        return [.. sim.Context.States];
    }
}

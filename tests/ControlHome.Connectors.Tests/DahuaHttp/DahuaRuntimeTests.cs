using System.Globalization;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.DahuaHttp;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using Microsoft.Extensions.Time.Testing;

namespace ControlHome.Connectors.Tests.DahuaHttp;

/// <summary>The runtime against a WireMock device: inventory, the event stream, resync, reconnects and the 401 policy.</summary>
public sealed class DahuaRuntimeTests : IDisposable
{
    private readonly FakeDahuaDevice _device = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _device.Dispose();

    [Fact]
    public async Task A_camera_reports_its_inventory_resyncs_and_maps_the_event_stream()
    {
        _device.Identity("VIP 1230 B G4");
        _device.Answer("/cgi-bin/configManager.cgi", "getConfig", ("name", "ChannelTitle")).WithBody("table.ChannelTitle[0].Name=Entrada\r\n");
        _device.Answer("/cgi-bin/configManager.cgi", "getConfig", ("name", "VideoAnalyseRule")).WithBody(DahuaFixtures.Text("video-analyse-rule.txt"));
        _device.Answer("/cgi-bin/alarm.cgi", "getInSlots").WithBody("result=1\r\n");
        _device.Answer("/cgi-bin/eventManager.cgi", "getEventIndexes", ("code", "VideoMotion")).WithStatusCode(400).WithBody("Error\r\n");
        _device.Answer("/cgi-bin/eventManager.cgi", "getEventIndexes", ("code", "AlarmLocal")).WithBody("channels[0]=0\r\n");
        _device.EventStream(DahuaFixtures.Multipart("event-stream-camera.txt"), once: true);
        var runtime = Runtime("camera");
        var context = new RecordingContext(TimeProvider.System);

        await using var run = DahuaHarness.Run(runtime, context);
        await DahuaHarness.WaitUntilAsync(() => runtime.EventsHandled >= 6, "the stream's events are handled");
        await DahuaHarness.WaitUntilAsync(() => Values(context, "tripwire", CapabilityIds.Motion).Count >= 3, "the line-crossing pulse ends");

        var device = context.Syncs.First().ShouldHaveSingleItem();
        device.ShouldBe("SYNTH00000001");
        context.Devices[0].Endpoints.Select(e => e.Key).ShouldBe(["main", "tripwire", "alarm1"]);
        context.Devices[0].Name.ShouldBe("Entrada");
        context.States.ShouldContain(s => s.CapabilityId == CapabilityIds.Camera && s.AttributeKey == "streamAvailable" && s.Value == StateValue.True);

        var main = Values(context, "main", CapabilityIds.Motion);
        main.Take(3).ShouldBe([false, true, false], "resync (none active), Start, Stop");
        Values(context, "tripwire", CapabilityIds.Motion).ShouldBe([false, true, false], "resync, the pulse, its end after the hold time");
        Values(context, "alarm1", CapabilityIds.Contact).Take(3).ShouldBe([true, true, false], "resync (active), Start, Stop");
        context.Availability.ShouldContain(("SYNTH00000001", Availability.Online));
        context.Statuses.ShouldContain(s => s.Status == IntegrationStatus.Ok);

        // Every CGI call was authenticated with Digest for the configured password.
        var attach = _device.Authenticated("/cgi-bin/eventManager.cgi", "attach")[0];
        FakeDahuaDevice.Query(attach, "codes").ShouldBe("[All]");
        FakeDahuaDevice.Query(attach, "heartbeat").ShouldBe("5");
        DahuaFixtures.IsValidDigest(FakeDahuaDevice.Header(attach, "Authorization")!, "GET", FakeDahuaDevice.User, FakeDahuaDevice.Password).ShouldBeTrue();
    }

    [Fact]
    public async Task A_dropped_stream_reconnects_with_backoff_and_reads_the_state_back()
    {
        _device.Identity("VIP 1230 B G4");
        _device.Answer("/cgi-bin/eventManager.cgi", "getEventIndexes", ("code", "VideoMotion")).WithBody("channels[0]=0\r\n");
        _device.EventStream(DahuaFixtures.Part("Code=VideoMotion;action=Stop;index=0"));
        var runtime = Runtime("camera");
        var context = new RecordingContext(TimeProvider.System);

        await using var run = DahuaHarness.Run(runtime, context);
        await DahuaHarness.WaitUntilAsync(() => runtime.StreamsOpened >= 3, "the stream is reopened after each drop");

        // Each connection reads motion back (active: true) before the stream's Stop arrives: nothing stays stale.
        _device.Authenticated("/cgi-bin/eventManager.cgi", "getEventIndexes").Count.ShouldBeGreaterThanOrEqualTo(2);
        Values(context, "main", CapabilityIds.Motion).Take(4).ShouldBe([true, false, true, false]);
        context.Statuses.ShouldContain(s => s.Status == IntegrationStatus.Degraded && s.Message!.Contains("reconnecting", StringComparison.Ordinal));
        context.Availability.ShouldContain(("SYNTH00000001", Availability.Unknown));
        context.Syncs.Count.ShouldBe(1, "the device is identified once per run, not on every reconnect");
        _device.Authenticated("/cgi-bin/magicBox.cgi", "getDeviceType").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_recorder_reports_channels_as_children_video_loss_and_new_smart_rule_endpoints()
    {
        _device.Identity("NVD 1404", serial: "SYNTHNVR0001", name: null);
        _device.Answer("/cgi-bin/configManager.cgi", "getConfig", ("name", "ChannelTitle")).WithBody(DahuaFixtures.Text("channel-title-recorder.txt"));
        _device.Answer("/cgi-bin/configManager.cgi", "getConfig", ("name", "VideoAnalyseRule")).WithBody(DahuaFixtures.Text("video-analyse-rule.txt"));
        _device.EventStream(DahuaFixtures.Multipart("event-stream-recorder.txt"), once: true);
        var runtime = Runtime("auto");
        var context = new RecordingContext(TimeProvider.System);

        await using var run = DahuaHarness.Run(runtime, context);
        await DahuaHarness.WaitUntilAsync(() => runtime.EventsHandled >= 7, "the stream's events are handled");

        context.Syncs[0].ShouldBe(["SYNTHNVR0001", "SYNTHNVR0001:ch1", "SYNTHNVR0001:ch2", "SYNTHNVR0001:ch3", "SYNTHNVR0001:ch4"]);
        context.States.ShouldContain(s => s.ExternalDeviceId == "SYNTHNVR0001" && s.AttributeKey == "channels" && s.Value == StateValue.Number(4));
        context.States.ShouldContain(s => s.ExternalDeviceId == "SYNTHNVR0001:ch3" && s.EndpointKey == "main" && s.Value == StateValue.True);
        // Video loss on channel 2 takes it offline until the loss ends (later the finite stream ends: all Unknown).
        var channel2 = context.Availability.Where(a => a.Device == "SYNTHNVR0001:ch2").Select(a => a.Availability).ToList();
        var lost = channel2.IndexOf(Availability.Offline);
        lost.ShouldBeGreaterThan(0, "online after the resync, then offline");
        channel2[lost + 1].ShouldBe(Availability.Online);

        // Channel 1's intrusion rule was not announced: its first event adds the endpoint, then reports on it.
        var upserted = context.Devices.Last(d => d.ExternalId == "SYNTHNVR0001:ch1");
        upserted.Endpoints.Select(e => e.Key).ShouldBe(["main", "tripwire", "intrusion"]);
        Values(context, "intrusion", CapabilityIds.Motion, "SYNTHNVR0001:ch1").ShouldBe([true, false]);
        context.States.ShouldNotContain(s => s.ExternalDeviceId.EndsWith(":ch10", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_access_terminal_reads_the_door_status_and_reports_access_events()
    {
        _device.Identity("SS 3530 MF FACE", serial: "SYNTH00000002");
        _device.Answer("/cgi-bin/accessControl.cgi", "getDoorStatus", ("channel", "1")).WithBody("Info.status=Close\r\n");
        _device.EventStream(DahuaFixtures.Multipart("event-stream-access.txt"), once: true);
        var runtime = Runtime("accessControl");
        var context = new RecordingContext(TimeProvider.System);

        await using var run = DahuaHarness.Run(runtime, context);
        await DahuaHarness.WaitUntilAsync(() => runtime.EventsHandled >= 5, "the stream's events are handled");

        Values(context, "main", CapabilityIds.Contact, "SYNTH00000002").Take(3).ShouldBe([false, true, false], "resync, DoorStatus Open, Close");
        context.Events.Select(e => e.Event).ShouldBe(["granted", "denied"]);
        context.Events.ShouldAllBe(e => e.CapabilityId == CapabilityIds.Generic && e.AttributeKey == "access" && e.EndpointKey == "main");
        context.Events[0].Data!.Value.GetRawText().ShouldNotContain("00000000");
        context.Devices.Last().Endpoints.Select(e => e.Key).ShouldBe(["main", "alarm2"], "the alarm input announced by its event");
    }

    [Fact]
    public async Task A_refused_login_waits_long_before_trying_again()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-21T12:00:00Z", CultureInfo.InvariantCulture));
        _device.RejectCredentials();
        var runtime = Runtime("camera", DahuaHarness.FastOptions with { AuthRetryDelay = TimeSpan.FromMinutes(30) });
        var context = new RecordingContext(time);

        await using var run = DahuaHarness.Run(runtime, context);
        await DahuaHarness.WaitUntilAsync(() => context.Statuses.Any(s => s.Status == IntegrationStatus.NeedsReauth), "the integration needs reauthorization");
        await Task.Delay(300, Ct);

        _device.Authenticated("/cgi-bin/magicBox.cgi", "getDeviceType").ShouldHaveSingleItem("one failed login only");
        context.Statuses.Last().Message!.ShouldContain("30 minutes");
        time.Advance(TimeSpan.FromMinutes(29));
        await Task.Delay(200, Ct);
        _device.Authenticated("/cgi-bin/magicBox.cgi", "getDeviceType").ShouldHaveSingleItem();

        time.Advance(TimeSpan.FromMinutes(2));
        await DahuaHarness.WaitUntilAsync(() => _device.Authenticated("/cgi-bin/magicBox.cgi", "getDeviceType").Count == 2, "it tries once more after the wait");
    }

    [Fact]
    public async Task An_unreachable_device_is_offline_and_retried_with_backoff()
    {
        var port = _device.Port;
        _device.Dispose();
        var runtime = new DahuaHttpConnectorFactory(null, null, DahuaHarness.FastOptions)
            .CreateRuntime(DahuaHarness.Instance(DahuaHarness.Config(port, "camera")));
        var context = new RecordingContext(TimeProvider.System);

        await using var run = DahuaHarness.Run(runtime, context);
        await DahuaHarness.WaitUntilAsync(() => context.Statuses.Count(s => s.Status == IntegrationStatus.Degraded) >= 3, "it keeps retrying");

        context.Statuses.Where(s => s.Status == IntegrationStatus.Degraded).ShouldAllBe(s => s.Message!.Contains("Cannot reach", StringComparison.Ordinal));
        context.Devices.ShouldBeEmpty("nothing is known about a device never reached");
    }

    [Fact]
    public async Task Stills_probe_the_camera_channel_numbering_once()
    {
        _device.Identity("VIP 1230 B G4");
        _device.EventStream(DahuaFixtures.Part("Heartbeat"));
        _device.Answer("/cgi-bin/snapshot.cgi", null, ("channel", "1")).WithStatusCode(400).WithBody("Error\r\n");
        _device.Answer("/cgi-bin/snapshot.cgi", null, ("channel", "0")).WithHeader("Content-Type", "image/jpeg").WithBody(DahuaFixtures.Jpeg);
        var runtime = Runtime("camera");
        var context = new RecordingContext(TimeProvider.System);
        await using var run = DahuaHarness.Run(runtime, context);
        await DahuaHarness.WaitUntilAsync(() => runtime.Inventory is not null, "the inventory is known");

        var first = await runtime.GetSnapshotAsync("SYNTH00000001", "main", Ct);
        var second = await runtime.GetSnapshotAsync("SYNTH00000001", "main", Ct);

        first.ShouldNotBeNull().ContentType.ShouldBe("image/jpeg");
        second.ShouldNotBeNull().Data.ToArray().ShouldBe(DahuaFixtures.Jpeg);
        _device.Authenticated("/cgi-bin/snapshot.cgi").Select(e => FakeDahuaDevice.Query(e, "channel")).ShouldBe(["1", "0", "0"]);
        (await runtime.GetSnapshotAsync("SYNTH00000001", "alarm1", Ct)).ShouldBeNull();
        (await runtime.GetSnapshotAsync("other", "main", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Live_streams_are_rtsp_urls_with_escaped_credentials_for_camera_endpoints_only()
    {
        _device.Identity("NVD 1404", serial: "SYNTHNVR0001");
        _device.Answer("/cgi-bin/configManager.cgi", "getConfig", ("name", "ChannelTitle")).WithBody(DahuaFixtures.Text("channel-title-recorder.txt"));
        _device.EventStream(DahuaFixtures.Part("Heartbeat"));
        var runtime = Runtime("recorder", extra: "\"rtspPort\":8554");
        var context = new RecordingContext(TimeProvider.System);
        (await runtime.GetStreamSourceAsync("SYNTHNVR0001:ch3", "main", CameraStreamQuality.Main, Ct)).ShouldBeNull("not running yet");
        await using var run = DahuaHarness.Run(runtime, context);
        await DahuaHarness.WaitUntilAsync(() => runtime.Inventory is not null, "the inventory is known");

        var main = await runtime.GetStreamSourceAsync("SYNTHNVR0001:ch3", "main", CameraStreamQuality.Main, Ct);
        var sub = await runtime.GetStreamSourceAsync("SYNTHNVR0001:ch3", "main", CameraStreamQuality.Sub, Ct);

        // The password has ':' and '@': both escaped in the user info (synthetic-Pa55%3Aw%40rd).
        Uri.EscapeDataString(FakeDahuaDevice.Password).ShouldBe("synthetic-Pa55%3Aw%40rd");
        main.ShouldNotBeNull().Url.ShouldBe($"rtsp://{FakeDahuaDevice.User}:{Uri.EscapeDataString(FakeDahuaDevice.Password)}@127.0.0.1:8554/cam/realmonitor?channel=3&subtype=0");
        sub.ShouldNotBeNull().Url.ShouldEndWith("channel=3&subtype=1");
        main.ToString().ShouldNotContain("synthetic");
        (await runtime.GetStreamSourceAsync("SYNTHNVR0001", "main", CameraStreamQuality.Main, Ct)).ShouldBeNull("the recorder itself is not a camera");
        (await runtime.GetStreamSourceAsync("SYNTHNVR0001:ch3", "tripwire", CameraStreamQuality.Main, Ct)).ShouldBeNull();
    }

    private DahuaRuntime Runtime(string kind, DahuaRuntimeOptions? options = null, string? extra = null) =>
        (DahuaRuntime)
        DahuaHarness.Factory(options).CreateRuntime(DahuaHarness.Instance(DahuaHarness.Config(_device.Port, kind, extra)));

    /// <summary>The boolean values reported for one attribute of an endpoint, in order.</summary>
    private static List<bool> Values(RecordingContext context, string endpoint, string capability, string device = "SYNTH00000001") =>
        [.. context.States.Where(s => s.ExternalDeviceId == device && s.EndpointKey == endpoint && s.CapabilityId == capability).Select(s => s.Value.B!.Value)];
}

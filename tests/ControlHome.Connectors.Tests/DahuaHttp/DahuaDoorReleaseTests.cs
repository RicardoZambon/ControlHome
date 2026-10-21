using System.Text.Json;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.DahuaHttp;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace ControlHome.Connectors.Tests.DahuaHttp;

/// <summary>
/// <c>doorOpener.open</c> on an SS access terminal is safety-critical: at most once — one authenticated request, never
/// retried or hedged, even when the terminal answers late or not at all; an idempotency key is required and a repeated one
/// never reaches the device.
/// </summary>
public sealed class DahuaDoorReleaseTests : IDisposable
{
    private const string Device = "SYNTH00000002";
    private const string AccessCgi = "/cgi-bin/accessControl.cgi";

    private static readonly JsonElement _empty = DahuaFixtures.Json("{}");

    private readonly FakeDahuaDevice _device = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _device.Dispose();

    [Fact]
    public async Task The_door_opens_with_one_remote_release_request()
    {
        _device.Answer(AccessCgi, "openDoor").WithBody("OK\r\n");
        await using var run = await StartAsync();

        var outcome = await run.Runtime.ExecuteAsync(Open("key-1"), Ct);

        outcome.ShouldBe(CommandOutcome.CompletedOutcome);
        var request = _device.Authenticated(AccessCgi, "openDoor").ShouldHaveSingleItem();
        (FakeDahuaDevice.Query(request, "channel"), FakeDahuaDevice.Query(request, "UserID"), FakeDahuaDevice.Query(request, "Type"))
            .ShouldBe(("1", "101", "Remote"));
        DahuaFixtures.IsValidDigest(FakeDahuaDevice.Header(request, "Authorization")!, "GET", FakeDahuaDevice.User, FakeDahuaDevice.Password).ShouldBeTrue();
        FakeDahuaDevice.Header(request, "Connection").ShouldBe("close");
    }

    [Fact]
    public async Task A_late_answer_cancelled_by_the_command_timeout_is_never_retried()
    {
        // The terminal answers after 3 s; the command service gives up after 0.5 s (its confirmation timeout).
        _device.Answer(AccessCgi, "openDoor").WithDelay(TimeSpan.FromSeconds(3)).WithBody("OK\r\n");
        await using var run = await StartAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(500));

        await Should.ThrowAsync<OperationCanceledException>(() => run.Runtime.ExecuteAsync(Open("key-2"), timeout.Token));
        await Task.Delay(TimeSpan.FromSeconds(4), Ct);

        // Exactly one authenticated request reached the terminal (the other one is the Digest challenge, refused before
        // anything happens), and nothing was sent again after the timeout.
        _device.Authenticated(AccessCgi, "openDoor").ShouldHaveSingleItem();
        _device.All(AccessCgi, "openDoor").Count.ShouldBe(2);

        // The key stays used: sending the same command again is a duplicate that never reaches the device.
        (await run.Runtime.ExecuteAsync(Open("key-2"), Ct)).ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe(CommandRejectionCodes.Duplicate);
        _device.All(AccessCgi, "openDoor").Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_terminal_that_does_not_answer_in_time_fails_without_a_retry()
    {
        _device.Answer(AccessCgi, "openDoor").WithDelay(TimeSpan.FromSeconds(3)).WithBody("OK\r\n");
        await using var run = await StartAsync(DahuaHarness.FastOptions with { CommandTimeout = TimeSpan.FromMilliseconds(500) });

        var outcome = await run.Runtime.ExecuteAsync(Open("key-3"), Ct);
        await Task.Delay(TimeSpan.FromSeconds(4), Ct);

        var rejected = outcome.ShouldBeOfType<CommandOutcome.Rejected>();
        rejected.Code.ShouldBe("timeout");
        rejected.Message.ShouldContain("may or may not have opened");
        _device.Authenticated(AccessCgi, "openDoor").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Refused_credentials_are_not_retried_and_need_reauthorization()
    {
        _device.Server.Given(Request.Create().UsingGet().WithPath(AccessCgi).WithParam("action", "openDoor").WithHeader("Authorization", "Digest *"))
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(401).WithBody("Error\r\n"));
        var context = new RecordingContext(TimeProvider.System);
        await using var run = await StartAsync(context: context);

        var outcome = await run.Runtime.ExecuteAsync(Open("key-4"), Ct);

        outcome.ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe(CommandRejectionCodes.Refused);
        _device.Authenticated(AccessCgi, "openDoor").ShouldHaveSingleItem();
        context.Statuses.ShouldContain(s => s.Status == IntegrationStatus.NeedsReauth);
    }

    [Fact]
    public async Task A_release_needs_an_idempotency_key_and_a_door_endpoint()
    {
        _device.Answer(AccessCgi, "openDoor").WithBody("OK\r\n");
        await using var run = await StartAsync();

        (await run.Runtime.ExecuteAsync(Open(string.Empty), Ct)).ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe(CommandRejectionCodes.IdempotencyRequired);
        (await run.Runtime.ExecuteAsync(Open("k", device: "other"), Ct)).ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe(CommandRejectionCodes.UnknownDevice);
        (await run.Runtime.ExecuteAsync(Open("k", endpoint: "alarm1"), Ct)).ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe(CommandRejectionCodes.UnknownEndpoint);
        var unlock = new DeviceCommandBatch(Device, [new DeviceCommand("main", CapabilityIds.Lock, "unlock", _empty)], "k");
        (await run.Runtime.ExecuteAsync(unlock, Ct)).ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe(CommandRejectionCodes.UnsupportedCommand);
        _device.All(AccessCgi, "openDoor").ShouldBeEmpty();
    }

    [Fact]
    public async Task The_xpe_relay_sends_its_own_url_once_without_digest()
    {
        _device.Server.Given(Request.Create().UsingGet().WithPath("/"))
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("<html></html>"));
        _device.Server.Given(Request.Create().UsingGet().WithPath("/fcgi/do").WithParam("action", "OpenDoor"))
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("OK"));
        var runtime = DahuaHarness.Factory(xpeRelay: true).CreateRuntime(DahuaHarness.Instance(DahuaHarness.Config(_device.Port, "xpe")));
        var context = new RecordingContext(TimeProvider.System);
        await using var run = DahuaHarness.Run(runtime, context);
        await DahuaHarness.WaitUntilAsync(() => context.Statuses.Any(s => s.Status == IntegrationStatus.Ok), "the station answers");
        var device = context.Devices.ShouldHaveSingleItem();
        device.DeviceClass.ShouldBe(DeviceClasses.Intercom);

        var outcome = await runtime.ExecuteAsync(new DeviceCommandBatch(device.ExternalId, [new DeviceCommand("main", CapabilityIds.DoorOpener, "open", _empty)], "x-1"), Ct);

        outcome.ShouldBe(CommandOutcome.CompletedOutcome);
        var relay = _device.All("/fcgi/do").ShouldHaveSingleItem();
        (FakeDahuaDevice.Query(relay, "UserName"), FakeDahuaDevice.Query(relay, "Password"), FakeDahuaDevice.Query(relay, "DoorNum"))
            .ShouldBe((FakeDahuaDevice.User, FakeDahuaDevice.Password, "1"));
        FakeDahuaDevice.Header(relay, "Authorization").ShouldBeNull();
    }



    private static DeviceCommandBatch Open(string key, string device = Device, string endpoint = "main") =>
        new(device, [new DeviceCommand(endpoint, CapabilityIds.DoorOpener, "open", _empty)], key);

    private async Task<RunningRuntime> StartAsync(DahuaRuntimeOptions? options = null, RecordingContext? context = null)
    {
        _device.Identity("SS 3530 MF FACE", serial: Device);
        _device.EventStream(DahuaFixtures.Part("Heartbeat"));
        context ??= new RecordingContext(TimeProvider.System);
        var runtime = (DahuaRuntime)DahuaHarness.Factory(options).CreateRuntime(DahuaHarness.Instance(DahuaHarness.Config(_device.Port, "accessControl")));
        var run = DahuaHarness.Run(runtime, context);
        await DahuaHarness.WaitUntilAsync(() => runtime.Inventory is not null, "the terminal is identified");
        return run;
    }
}

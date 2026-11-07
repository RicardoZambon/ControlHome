using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.DahuaHttp;
using ControlHome.Connectors.DahuaHttp.Protocol;
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
    public async Task After_refused_credentials_no_release_reaches_the_terminal_until_the_retry_time()
    {
        _device.Server.Given(Request.Create().UsingGet().WithPath(AccessCgi).WithParam("action", "openDoor").WithHeader("Authorization", "Digest *"))
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(401).WithBody("Error\r\n"));
        await using var run = await StartAsync();
        (await run.Runtime.ExecuteAsync(Open("key-5"), Ct)).ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe(CommandRejectionCodes.Refused);

        // Pressing the button again (a new key) must not count as another failed login on the terminal.
        var again = (await run.Runtime.ExecuteAsync(Open("key-6"), Ct)).ShouldBeOfType<CommandOutcome.Rejected>();

        again.Code.ShouldBe(CommandRejectionCodes.Refused);
        again.Message.ShouldContain("not released");
        _device.All(AccessCgi, "openDoor").Count.ShouldBe(2, "the first release's challenge and its refused login, nothing after");
    }

    [Fact]
    public async Task A_connection_dropped_after_the_release_was_sent_is_never_sent_again()
    {
        // The terminal keeps the Digest challenge's connection open (keep-alive), reads the authenticated release and
        // closes the connection without answering: it may have opened the door. An HTTP stack that had reused the
        // keep-alive connection would silently send the release again on a new one.
        await using var terminal = new DroppingTerminal();
        using var client = new DahuaHttpClient(
            new DahuaConnection(new Uri($"http://127.0.0.1:{terminal.Port}/"), FakeDahuaDevice.User, FakeDahuaDevice.Password, TimeProvider.System),
            DahuaHarness.FastOptions);

        var failure = await Should.ThrowAsync<DahuaHttpException>(() => client.OpenDoorAsync(1, "101", Ct));
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);

        // Sent, then no answer: the door may have opened (never "not released"), and it was sent exactly once.
        failure.Failure.ShouldBe(DahuaFailure.NoAnswer);
        terminal.AuthenticatedRequests.ShouldBe(1);
        terminal.Connections.ShouldBe(2, "the challenge and the release, each on its own connection");
    }

    [Fact]
    public async Task A_terminal_offering_only_basic_auth_never_gets_the_password()
    {
        _device.Server.Given(Request.Create().UsingGet().WithPath(AccessCgi).WithParam("action", "openDoor"))
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(401).WithHeader("WWW-Authenticate", "Basic realm=\"SS\"").WithBody("Error\r\n"));
        await using var run = await StartAsync();

        (await run.Runtime.ExecuteAsync(Open("key-8"), Ct)).ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe(CommandRejectionCodes.Refused);

        var request = _device.All(AccessCgi, "openDoor").ShouldHaveSingleItem("the challenge only");
        FakeDahuaDevice.Header(request, "Authorization").ShouldBeNull();
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

    /// <summary>
    /// A minimal HTTP/1.1 server on a raw socket: a request without credentials gets the Digest challenge and the
    /// connection stays open for the next one; an authenticated request is counted and its connection dropped unanswered.
    /// </summary>
    private sealed class DroppingTerminal : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accepting;
        private int _authenticated;
        private int _connections;

        public DroppingTerminal()
        {
            _listener.Start();
            _accepting = AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        /// <summary>Requests that carried a valid Digest answer for the fake device's password.</summary>
        public int AuthenticatedRequests => Volatile.Read(ref _authenticated);

        public int Connections => Volatile.Read(ref _connections);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _accepting.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                var connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                Interlocked.Increment(ref _connections);
                _ = ServeAsync(connection);
            }
        }

        private async Task ServeAsync(TcpClient connection)
        {
            using (connection)
            {
                var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                try
                {
                    while (true)
                    {
                        var headers = new List<string>();
                        string? line;
                        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_stop.Token)))
                        {
                            headers.Add(line);
                        }

                        if (headers.Count == 0)
                        {
                            return;
                        }

                        if (headers.FirstOrDefault(h => h.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) is { } authorization)
                        {
                            // Acts on it (opens the door), then drops the connection without answering.
                            if (DahuaFixtures.IsValidDigest(authorization["Authorization:".Length..].Trim(), "GET", FakeDahuaDevice.User, FakeDahuaDevice.Password))
                            {
                                Interlocked.Increment(ref _authenticated);
                            }

                            return;
                        }

                        var challenge = "HTTP/1.1 401 Unauthorized\r\n"
                            + $"WWW-Authenticate: Digest realm=\"{FakeDahuaDevice.Realm}\", qop=\"auth\", nonce=\"{Guid.NewGuid():N}\", opaque=\"\"\r\n"
                            + "Content-Length: 0\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(challenge), _stop.Token);
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    // The client went away.
                }
            }
        }
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

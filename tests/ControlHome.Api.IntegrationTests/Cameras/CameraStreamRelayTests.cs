using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using ControlHome.Api.Features.Cameras;
using Microsoft.Extensions.Time.Testing;

namespace ControlHome.Api.IntegrationTests.Cameras;

/// <summary>
/// The live-view relay on its own, between two real WebSocket pairs over loopback TCP (no HTTP, no database): a session
/// that ends closes the browser's view and go2rtc's socket, go2rtc closing ends the view, and go2rtc's text messages
/// reach the browser only through the redactor.
/// </summary>
public sealed class CameraStreamRelayTests : IAsyncDisposable
{
    private static readonly TimeSpan _recheck = TimeSpan.FromMinutes(1);

    // Synthetic sources an error might quote, built at run time (the secret scanner reads literal user:pass@ URLs).
    private static readonly string _scheme = string.Concat("rtsp", "://");

    private readonly List<IDisposable> _resources = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync()
    {
        foreach (var resource in _resources)
        {
            resource.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_session_that_ended_closes_the_view_and_go2rtcs_socket()
    {
        var (browser, relayClient) = await PairAsync();
        var (relayUpstream, go2rtc) = await PairAsync();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-21T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var valid = true;
        var relay = CameraStreamRelay.RunAsync(relayClient, relayUpstream, text => text, _ => Task.FromResult(valid), _recheck, time, Ct);

        await SendAsync(browser, """{"type":"mse","value":"avc1.640029"}""");
        (await ReceiveAsync(go2rtc)).Text.ShouldBe("""{"type":"mse","value":"avc1.640029"}""");

        // Still valid at the first check: nothing happens.
        time.Advance(_recheck);
        await Task.Delay(200, Ct);
        relay.IsCompleted.ShouldBeFalse();

        var closing = ReceiveAsync(browser);
        valid = false;
        await AdvanceUntilAsync(time, closing);

        (await closing).Type.ShouldBe(WebSocketMessageType.Close);
        browser.CloseStatus.ShouldBe(WebSocketCloseStatus.PolicyViolation);
        await browser.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ok", Ct);
        await AdvanceUntilAsync(time, relay);
        await relay;
        await ShouldBeGoneAsync(go2rtc);
    }

    [Fact]
    public async Task Go2rtc_closing_ends_the_view_and_its_text_is_redacted()
    {
        var (browser, relayClient) = await PairAsync();
        var (relayUpstream, go2rtc) = await PairAsync();
        var relay = CameraStreamRelay.RunAsync(
            relayClient,
            relayUpstream,
            text => text.Replace("cam-secret", "***", StringComparison.Ordinal),
            _ => Task.FromResult(true),
            _recheck,
            TimeProvider.System,
            Ct);

        await go2rtc.SendAsync(new byte[] { 0, 0, 0, 24, 0x66, 0x74, 0x79, 0x70 }, WebSocketMessageType.Binary, endOfMessage: true, Ct);
        (await ReceiveAsync(browser)).Type.ShouldBe(WebSocketMessageType.Binary);

        // A text message in two frames: redacted as a whole.
        await go2rtc.SendAsync(Encoding.UTF8.GetBytes($$"""{"type":"error","value":"{{_scheme}}viewer:cam-"""), WebSocketMessageType.Text, endOfMessage: false, Ct);
        await go2rtc.SendAsync(Encoding.UTF8.GetBytes("""secret@192.0.2.10/x"}"""), WebSocketMessageType.Text, endOfMessage: true, Ct);
        var error = await ReceiveAsync(browser);
        error.Text.ShouldBe($$"""{"type":"error","value":"{{_scheme}}viewer:***@192.0.2.10/x"}""");

        await go2rtc.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", Ct);
        (await ReceiveAsync(browser)).Type.ShouldBe(WebSocketMessageType.Close);
        browser.CloseStatus.ShouldBe(WebSocketCloseStatus.EndpointUnavailable);
        await relay.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task Signalling_reaches_the_browser_untouched_while_errors_are_redacted()
    {
        // A short secret somewhere in the app (a 4-digit camera password) must not mangle WebRTC signalling that happens
        // to contain it (here a port), or the browser's ICE fails; only go2rtc's errors can quote a source.
        var (browser, relayClient) = await PairAsync();
        var (relayUpstream, go2rtc) = await PairAsync();
        var relay = CameraStreamRelay.RunAsync(
            relayClient,
            relayUpstream,
            text => text.Replace("1234", "***", StringComparison.Ordinal),
            _ => Task.FromResult(true),
            _recheck,
            TimeProvider.System,
            Ct);

        const string Candidate = """{"type":"webrtc/candidate","value":"candidate:1 1 udp 2113937151 192.0.2.5 51234 typ host"}""";
        const string Answer = """{"type":"webrtc/answer","value":"v=0\r\no=- 1234 1 IN IP4 192.0.2.5\r\n"}""";
        const string Codecs = """{"type":"mse","value":"video/mp4; codecs=\"avc1.641234\""}""";
        foreach (var message in new[] { Candidate, Answer, Codecs })
        {
            await SendAsync(go2rtc, message);
            (await ReceiveAsync(browser)).Text.ShouldBe(message);
        }

        await SendAsync(go2rtc, $$"""{"type":"error","value":"{{_scheme}}admin:1234@192.0.2.10"}""");
        (await ReceiveAsync(browser)).Text.ShouldBe($$"""{"type":"error","value":"{{_scheme}}admin:***@192.0.2.10"}""");
        await SendAsync(go2rtc, $"streams: {_scheme}admin:1234@192.0.2.10");
        (await ReceiveAsync(browser)).Text.ShouldBe($"streams: {_scheme}admin:***@192.0.2.10");

        await browser.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", Ct);
        await relay.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task A_binary_message_from_the_browser_closes_the_view()
    {
        var (browser, relayClient) = await PairAsync();
        var (relayUpstream, go2rtc) = await PairAsync();
        var relay = CameraStreamRelay.RunAsync(relayClient, relayUpstream, text => text, _ => Task.FromResult(true), _recheck, TimeProvider.System, Ct);

        await browser.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, endOfMessage: true, Ct);

        (await ReceiveAsync(browser)).Type.ShouldBe(WebSocketMessageType.Close);
        browser.CloseStatus.ShouldBe(WebSocketCloseStatus.InvalidMessageType);
        await relay.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await ShouldBeGoneAsync(go2rtc);
    }

    /// <summary>Moves the fake clock on (the relay's timers) until <paramref name="task"/> completes.</summary>
    private static async Task AdvanceUntilAsync(FakeTimeProvider time, Task task)
    {
        for (var i = 0; i < 100 && !task.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(50, Ct);
        }

        task.IsCompleted.ShouldBeTrue();
    }

    /// <summary>go2rtc's end sees the relay's socket closed or dropped (a consumer that stays open would hold the camera).</summary>
    private static async Task ShouldBeGoneAsync(WebSocket go2rtc)
    {
        try
        {
            (await ReceiveAsync(go2rtc)).Type.ShouldBe(WebSocketMessageType.Close);
        }
        catch (WebSocketException)
        {
            // Dropped without a close frame: gone too.
        }
    }

    /// <summary>A connected WebSocket pair (client end, server end) over loopback TCP, without an HTTP handshake.</summary>
    private async Task<(WebSocket Client, WebSocket Server)> PairAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepting = listener.AcceptTcpClientAsync(Ct);
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, Ct);
        var server = await accepting;
        _resources.Add(client);
        _resources.Add(server);
        var clientSocket = WebSocket.CreateFromStream(client.GetStream(), new WebSocketCreationOptions { IsServer = false, KeepAliveInterval = Timeout.InfiniteTimeSpan });
        var serverSocket = WebSocket.CreateFromStream(server.GetStream(), new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = Timeout.InfiniteTimeSpan });
        _resources.Add(clientSocket);
        _resources.Add(serverSocket);
        return (clientSocket, serverSocket);
    }

    private static Task SendAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, Ct);

    private static async Task<(WebSocketMessageType Type, string Text)> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage || result.MessageType == WebSocketMessageType.Close)
            {
                return (result.MessageType, Encoding.UTF8.GetString(message.ToArray()));
            }
        }
    }
}

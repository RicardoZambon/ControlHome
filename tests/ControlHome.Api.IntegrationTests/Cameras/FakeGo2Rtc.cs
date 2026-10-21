using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ControlHome.Api.IntegrationTests.Cameras;

/// <summary>One request go2rtc received.</summary>
public sealed record Go2RtcRequest(string Method, string Path, string Query, string? Authorization, bool WebSocket)
{
    public string? Param(string name) => Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(Query).TryGetValue(name, out var values) ? values.ToString() : null;
}

/// <summary>
/// A stand-in for the go2rtc sidecar on a loopback Kestrel port, recording every request (so tests can prove nothing but
/// its three viewer paths is ever used): <c>PATCH /api/streams</c> → 200, <c>POST /api/webrtc</c> → a JSON SDP answer,
/// <c>GET /api/ws</c> (WebSocket) → answers each text message with an <c>mse</c> message and a binary segment; anything
/// else 404. Every request must carry the Basic credentials (<c>local_auth</c>), else 401.
/// </summary>
public sealed class FakeGo2Rtc : IAsyncDisposable
{
    public static readonly string User = "it-" + Guid.NewGuid().ToString("N")[..8];
    // Random per run: nothing secret-looking in the repository.
    public static readonly string Password = Guid.NewGuid().ToString("N");
    public const string Answer = "v=0\r\no=- 1 1 IN IP4 192.0.2.5\r\ns=-\r\nt=0 0\r\n";

    private static readonly byte[] _segment = [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70];

    private readonly WebApplication _app;

    private FakeGo2Rtc(WebApplication app) => _app = app;

    public ConcurrentQueue<Go2RtcRequest> Requests { get; } = new();

    /// <summary>The text messages the relay forwarded on go2rtc's WebSocket.</summary>
    public ConcurrentQueue<string> SocketMessages { get; } = new();

    public string Url { get; private set; } = string.Empty;

    public static string ExpectedAuthorization => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}"));

    public static async Task<FakeGo2Rtc> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var fake = new FakeGo2Rtc(app);
        app.UseWebSockets();
        app.Run(fake.HandleAsync);
        await app.StartAsync();
        fake.Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return fake;
    }

    public void Clear()
    {
        Requests.Clear();
        SocketMessages.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var authorization = request.Headers.Authorization.ToString();
        Requests.Enqueue(new Go2RtcRequest(
            request.Method,
            request.Path.Value ?? string.Empty,
            request.QueryString.Value ?? string.Empty,
            string.IsNullOrEmpty(authorization) ? null : authorization,
            context.WebSockets.IsWebSocketRequest));
        if (authorization != ExpectedAuthorization)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        switch (request.Method, request.Path.Value)
        {
            case ("PATCH", "/api/streams"):
                context.Response.StatusCode = StatusCodes.Status200OK;
                return;
            case ("POST", "/api/webrtc"):
                await context.Response.WriteAsJsonAsync(new { type = "answer", sdp = Answer });
                return;
            case ("GET", "/api/ws") when context.WebSockets.IsWebSocketRequest:
                using (var socket = await context.WebSockets.AcceptWebSocketAsync())
                {
                    await EchoAsync(socket, context.RequestAborted);
                }

                return;
            default:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
        }
    }

    private async Task EchoAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
                    return;
                }

                SocketMessages.Enqueue(Encoding.UTF8.GetString(buffer, 0, result.Count));
                await socket.SendAsync("""{"type":"mse","value":"video/mp4; codecs=\"avc1.640029\""}"""u8.ToArray(), WebSocketMessageType.Text, true, cancellationToken);
                await socket.SendAsync(_segment, WebSocketMessageType.Binary, true, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // The relay went away.
        }
    }
}

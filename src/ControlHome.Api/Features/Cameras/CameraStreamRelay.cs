using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ControlHome.Application.Devices;

namespace ControlHome.Api.Features.Cameras;

/// <summary>
/// Relays one live view between the browser's WebSocket and go2rtc's (<c>/api/ws?src=&lt;opaque name&gt;</c>, opened by
/// the server). Restreamer → browser: every frame as is (MSE/MP4 segments, JSON signalling). Browser → restreamer: only
/// text messages of go2rtc's viewer protocol (<c>{"type": "mse" | "mp4" | "mjpeg" | "webrtc/offer" | "webrtc/candidate",
/// "value": …}</c>, at most <see cref="MaxClientMessageBytes"/>), a WebRTC offer only when it is receive-only — so a
/// viewer can neither publish into a camera's stream nor talk through its speaker; anything else is dropped, and a
/// binary or oversized message closes the relay. The user's session is re-checked every
/// <see cref="Hosting.IdentitySetup.SecurityStampValidationInterval"/>; a disabled, deleted or demoted user's view closes.
/// </summary>
internal static class CameraStreamRelay
{
    public const int MaxClientMessageBytes = 64 * 1024;

    private const int UpstreamBufferBytes = 64 * 1024;

    private static readonly HashSet<string> _clientTypes = new(StringComparer.Ordinal) { "mse", "mp4", "mjpeg", "webrtc/offer", "webrtc/candidate" };

    /// <summary>Runs until either side closes, the session ends or <paramref name="cancellationToken"/> fires; then closes both.</summary>
    /// <param name="sessionValid">Whether the viewer may still watch (checked every <paramref name="recheckInterval"/>).</param>
    public static async Task RunAsync(
        WebSocket client,
        WebSocket upstream,
        Func<CancellationToken, Task<bool>> sessionValid,
        TimeSpan recheckInterval,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var toUpstream = PumpFromClientAsync(client, upstream, stop.Token);
        var toClient = PumpFromUpstreamAsync(upstream, client, stop.Token);
        var watch = WatchSessionAsync(sessionValid, recheckInterval, time, stop.Token);

        var first = await Task.WhenAny(toUpstream, toClient, watch).ConfigureAwait(false);
        var (status, reason) = first == watch
            ? (WebSocketCloseStatus.PolicyViolation, "session ended")
            : await Outcome(first).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        foreach (var task in new[] { toUpstream, toClient, watch })
        {
            await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        await CloseAsync(client, status, reason).ConfigureAwait(false);
        await CloseAsync(upstream, WebSocketCloseStatus.NormalClosure, "viewer left").ConfigureAwait(false);
    }

    /// <summary>The close status a pump ended with (normal when the other side closed).</summary>
    private static async Task<(WebSocketCloseStatus Status, string Reason)> Outcome(Task first)
    {
        try
        {
            return first is Task<(WebSocketCloseStatus, string)> ended ? await ended.ConfigureAwait(false) : (WebSocketCloseStatus.NormalClosure, "closed");
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
            return (WebSocketCloseStatus.EndpointUnavailable, "stream lost");
        }
    }

    private static async Task<(WebSocketCloseStatus, string)> PumpFromClientAsync(WebSocket client, WebSocket upstream, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4 * 1024);
        var message = new MemoryStream();
        try
        {
            while (true)
            {
                var result = await client.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return (WebSocketCloseStatus.NormalClosure, "viewer closed");
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    return (WebSocketCloseStatus.InvalidMessageType, "binary messages are not accepted");
                }

                if (message.Length + result.Count > MaxClientMessageBytes)
                {
                    return (WebSocketCloseStatus.MessageTooBig, "message too large");
                }

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var text = message.ToArray();
                message.SetLength(0);
                if (IsAllowed(text))
                {
                    await upstream.SendAsync(text, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<(WebSocketCloseStatus, string)> PumpFromUpstreamAsync(WebSocket upstream, WebSocket client, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(UpstreamBufferBytes);
        try
        {
            while (true)
            {
                var result = await upstream.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return (WebSocketCloseStatus.EndpointUnavailable, "stream ended");
                }

                await client.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task WatchSessionAsync(Func<CancellationToken, Task<bool>> sessionValid, TimeSpan interval, TimeProvider time, CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(interval, time, cancellationToken).ConfigureAwait(false);
            if (!await sessionValid(cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>A viewer-protocol message go2rtc may get: a JSON object with an allowed <c>type</c> (offers receive-only).</summary>
    internal static bool IsAllowed(ReadOnlySpan<byte> text)
    {
        try
        {
            var reader = new Utf8JsonReader(text, new JsonReaderOptions { MaxDepth = 8 });
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() is not { } name
                || !_clientTypes.Contains(name))
            {
                return false;
            }

            if (name == "webrtc/offer")
            {
                return root.TryGetProperty("value", out var sdp)
                    && sdp.ValueKind == JsonValueKind.String
                    && sdp.GetString() is { } offer
                    && offer.StartsWith("v=0", StringComparison.Ordinal)
                    && SdpOffers.IsReceiveOnly(offer);
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(status, Truncate(reason), timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Already gone.
        }
        finally
        {
            if (socket.State != WebSocketState.Closed)
            {
                socket.Abort();
            }
        }
    }

    /// <summary>A close reason is at most 123 bytes of UTF-8.</summary>
    private static string Truncate(string reason) => Encoding.UTF8.GetByteCount(reason) <= 123 ? reason : reason[..60];
}

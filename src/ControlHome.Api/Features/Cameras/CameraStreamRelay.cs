using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ControlHome.Application.Devices;

namespace ControlHome.Api.Features.Cameras;

/// <summary>
/// Relays one live view between the browser's WebSocket and go2rtc's (<c>/api/ws?src=&lt;opaque name&gt;</c>, opened by
/// the server).
/// <list type="bullet">
/// <item>Restreamer → browser: binary frames (MSE/MP4 segments) as they come; text messages whole — signalling (codecs,
/// SDP answers, ICE candidates) as is, anything else (errors) through the log redactor, so no camera credential a go2rtc
/// error might quote reaches the browser.</item>
/// <item>Browser → restreamer: only text messages of go2rtc's viewer protocol (<c>{"type": "mse" | "mp4" | "mjpeg" |
/// "webrtc/offer" | "webrtc/candidate", "value": "…"}</c>, at most <see cref="MaxClientMessageBytes"/>), <b>rewritten</b>
/// with only those two members (<see cref="Canonicalize"/>): go2rtc reads JSON with Go's <c>encoding/json</c>, whose keys
/// match case-insensitively and whose last duplicate wins, so the bytes go2rtc parses are never the browser's own. A
/// WebRTC offer passes only when it is receive-only — a viewer can neither publish into a camera's stream nor talk through
/// its speaker — and a view starts at most <see cref="MaxStreamStartsPerView"/> streams (each <c>mse</c>, <c>mp4</c>,
/// <c>mjpeg</c> or offer adds a go2rtc consumer). Anything else is dropped; a binary or oversized message closes the
/// relay.</item>
/// </list>
/// The user's session is re-checked every <see cref="Hosting.IdentitySetup.SecurityStampValidationInterval"/>; a
/// disabled, deleted or demoted user's view closes. On the way out go2rtc's socket goes first (it drops the consumer and
/// the camera connection), then the browser gets a close frame with the reason.
/// </summary>
internal static class CameraStreamRelay
{
    public const int MaxClientMessageBytes = 64 * 1024;

    /// <summary>The longest text message relayed from go2rtc (an SDP answer is a few KB).</summary>
    public const int MaxUpstreamTextBytes = 256 * 1024;

    /// <summary>The streams one view may start (video-rtc.js starts one or two: MSE and/or WebRTC).</summary>
    public const int MaxStreamStartsPerView = 4;

    private const int UpstreamBufferBytes = 64 * 1024;

    private static readonly TimeSpan _closeTimeout = TimeSpan.FromSeconds(2);

    private static readonly HashSet<string> _clientTypes = new(StringComparer.Ordinal) { "mse", "mp4", "mjpeg", "webrtc/offer", "webrtc/candidate" };

    private static readonly HashSet<string> _streamStarts = new(StringComparer.Ordinal) { "mse", "mp4", "mjpeg", "webrtc/offer" };

    /// <summary>go2rtc's signalling messages to a viewer: codecs, SDP answers and ICE candidates (never a stream source).</summary>
    private static readonly HashSet<string> _signalling = new(StringComparer.Ordinal) { "mse", "mp4", "mjpeg", "webrtc", "webrtc/answer", "webrtc/candidate" };

    private static readonly JsonDocumentOptions _read = new() { MaxDepth = 8 };

    private static readonly JsonWriterOptions _write = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Runs until either side closes, the session ends or <paramref name="cancellationToken"/> fires; then closes both.
    /// </summary>
    /// <param name="redact">Masks known secrets in a text message from the restreamer (the log redactor).</param>
    /// <param name="sessionValid">Whether the viewer may still watch (checked every <paramref name="recheckInterval"/>).</param>
    public static async Task RunAsync(
        WebSocket client,
        WebSocket upstream,
        Func<string, string> redact,
        Func<CancellationToken, Task<bool>> sessionValid,
        TimeSpan recheckInterval,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        // Two stops: cancelling a WebSocket operation aborts that socket, so go2rtc's side is stopped first while the
        // browser's socket stays usable for its close frame.
        using var upstreamStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var clientStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var toUpstream = PumpFromClientAsync(client, upstream, clientStop.Token, upstreamStop.Token);
        var toClient = PumpFromUpstreamAsync(upstream, client, redact, upstreamStop.Token, clientStop.Token);
        var watch = WatchSessionAsync(sessionValid, recheckInterval, time, upstreamStop.Token);

        var first = await Task.WhenAny(toUpstream, toClient, watch).ConfigureAwait(false);
        var (status, reason) = first == watch
            ? (WebSocketCloseStatus.PolicyViolation, "session ended")
            : await OutcomeAsync(first).ConfigureAwait(false);

        await upstreamStop.CancelAsync().ConfigureAwait(false);

        // The browser pump may be in the middle of a send to a slow browser: give it a moment, then abort.
        await WaitBrieflyAsync(toClient, time).ConfigureAwait(false);
        if (!toClient.IsCompleted)
        {
            await clientStop.CancelAsync().ConfigureAwait(false);
        }

        await CloseAsync(client, status, reason, time).ConfigureAwait(false);

        // The browser answers the close frame (its pump then ends); a browser that does not is cut off.
        await WaitBrieflyAsync(toUpstream, time).ConfigureAwait(false);
        await clientStop.CancelAsync().ConfigureAwait(false);
        foreach (var task in new[] { toUpstream, toClient, watch })
        {
            await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        if (upstream.State != WebSocketState.Closed)
        {
            upstream.Abort();
        }
    }

    /// <summary>
    /// A viewer-protocol message go2rtc may get, rewritten as <c>{"type":…,"value":…}</c> (members other than those two
    /// dropped), or <c>null</c>: not one JSON object, an unknown type, a <c>type</c> or <c>value</c> that appears twice or
    /// in another letter case (Go would read it), a value that is not a string, or an offer that is not receive-only.
    /// </summary>
    internal static ViewerMessage? Canonicalize(ReadOnlyMemory<byte> text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, _read);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? type = null;
            string? value = null;
            var hasType = false;
            var hasValue = false;
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals("type"))
                {
                    if (hasType || property.Value.ValueKind != JsonValueKind.String)
                    {
                        return null;
                    }

                    hasType = true;
                    type = property.Value.GetString();
                }
                else if (property.NameEquals("value"))
                {
                    if (hasValue || property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    {
                        return null;
                    }

                    hasValue = true;
                    value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                }
                else if (string.Equals(property.Name, "type", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "value", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            if (type is null || !_clientTypes.Contains(type))
            {
                return null;
            }

            if (type == "webrtc/offer" && !(value is not null && value.StartsWith("v=0", StringComparison.Ordinal) && SdpOffers.IsReceiveOnly(value)))
            {
                return null;
            }

            var output = new ArrayBufferWriter<byte>(text.Length + 32);
            using (var writer = new Utf8JsonWriter(output, _write))
            {
                writer.WriteStartObject();
                writer.WriteString("type", type);
                if (value is not null)
                {
                    writer.WriteString("value", value);
                }

                writer.WriteEndObject();
            }

            return new ViewerMessage(type, output.WrittenSpan.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A go2rtc text message that is a JSON object whose <c>type</c> is viewer signalling (<see cref="_signalling"/>).</summary>
    private static bool IsSignalling(string message)
    {
        try
        {
            using var document = JsonDocument.Parse(message, _read);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && _signalling.Contains(type.GetString()!);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The close status a pump ended with (normal when the other side closed).</summary>
    private static async Task<(WebSocketCloseStatus Status, string Reason)> OutcomeAsync(Task first)
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

    private static async Task<(WebSocketCloseStatus, string)> PumpFromClientAsync(
        WebSocket client,
        WebSocket upstream,
        CancellationToken clientToken,
        CancellationToken upstreamToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4 * 1024);
        var message = new MemoryStream();
        var starts = 0;
        try
        {
            while (true)
            {
                var result = await client.ReceiveAsync(buffer, clientToken).ConfigureAwait(false);
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

                var viewer = Canonicalize(message.ToArray());
                message.SetLength(0);
                if (viewer is null || (_streamStarts.Contains(viewer.Type) && ++starts > MaxStreamStartsPerView))
                {
                    continue;
                }

                await upstream.SendAsync(viewer.Json, WebSocketMessageType.Text, endOfMessage: true, upstreamToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<(WebSocketCloseStatus, string)> PumpFromUpstreamAsync(
        WebSocket upstream,
        WebSocket client,
        Func<string, string> redact,
        CancellationToken upstreamToken,
        CancellationToken clientToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(UpstreamBufferBytes);
        var text = new MemoryStream();
        try
        {
            while (true)
            {
                var result = await upstream.ReceiveAsync(buffer, upstreamToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return (WebSocketCloseStatus.EndpointUnavailable, "stream ended");
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    await client.SendAsync(buffer.AsMemory(0, result.Count), WebSocketMessageType.Binary, result.EndOfMessage, clientToken).ConfigureAwait(false);
                    continue;
                }

                if (text.Length + result.Count > MaxUpstreamTextBytes)
                {
                    return (WebSocketCloseStatus.InternalServerError, "stream message too large");
                }

                text.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var message = Encoding.UTF8.GetString(text.GetBuffer(), 0, (int)text.Length);
                text.SetLength(0);

                // Signalling passes untouched (masking a short secret inside an SDP or a candidate would break the view);
                // anything else — go2rtc's errors, which may quote a stream's source — goes through the redactor.
                var safe = IsSignalling(message) ? message : redact(message);
                await client.SendAsync(Encoding.UTF8.GetBytes(safe), WebSocketMessageType.Text, endOfMessage: true, clientToken).ConfigureAwait(false);
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

    private static async Task WaitBrieflyAsync(Task task, TimeProvider time)
    {
        if (!task.IsCompleted)
        {
            await task.WaitAsync(_closeTimeout, time, CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason, TimeProvider time)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(_closeTimeout, time);
                await socket.CloseOutputAsync(status, Truncate(reason), timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Already gone.
        }
        finally
        {
            if (socket.State is not (WebSocketState.Closed or WebSocketState.CloseSent))
            {
                socket.Abort();
            }
        }
    }

    /// <summary>A close reason is at most 123 bytes of UTF-8.</summary>
    private static string Truncate(string reason) => Encoding.UTF8.GetByteCount(reason) <= 123 ? reason : reason[..60];

    /// <summary>A rewritten viewer message: its type and the JSON go2rtc gets.</summary>
    internal sealed record ViewerMessage(string Type, byte[] Json);
}

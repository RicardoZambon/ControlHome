
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ControlHome.Application.Devices;
using ControlHome.Application.Security;
using ControlHome.Connectors.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlHome.Infrastructure.Cameras;

/// <summary>
/// Where the go2rtc sidecar's API is and how to sign in to it (section <see cref="SectionName"/>, e.g.
/// <c>Cameras__Go2Rtc__Url=http://127.0.0.1:1984</c>). Live view is off unless all three are set. go2rtc listens on
/// loopback only (or, in the development compose, on the project network without a published port), with random
/// credentials and <c>local_auth</c>, so nothing but this server can use it.
/// </summary>
public sealed class Go2RtcOptions
{
    public const string SectionName = "Cameras:Go2Rtc";

    /// <summary>The API base address (<c>http://127.0.0.1:1984</c>); no user info, query or fragment.</summary>
    public string? Url { get; set; }

    public string? Username { get; set; }

    /// <summary>Secret (registered with the log redactor at startup).</summary>
    public string? Password { get; set; }

    /// <summary>Timeout of one API call (stream registration, WebRTC exchange, WebSocket handshake).</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The most live views relayed at once (each holds a go2rtc consumer).</summary>
    public int MaxLiveSessions { get; set; } = 32;
}

/// <summary>
/// <see cref="ICameraRestreamer"/> over go2rtc's HTTP API, using exactly three of its paths and nothing else:
/// <list type="bullet">
/// <item><c>PATCH /api/streams?name=&amp;src=</c> registers (or re-points) a stream <b>in memory only</b> — unlike
/// <c>PUT</c>, go2rtc 1.9 does not write a <c>PATCH</c> to its configuration file, so camera URLs and credentials never
/// land on disk (the mounted <c>go2rtc.yaml</c> is read-only anyway);</item>
/// <item><c>POST /api/webrtc?src=</c> exchanges a receive-only SDP offer for an answer (JSON);</item>
/// <item><c>GET /api/ws?src=</c> (WebSocket) carries MSE segments and WebRTC signalling (go2rtc's video-rtc.js protocol).</item>
/// </list>
/// Every call sends HTTP Basic credentials (go2rtc's <c>local_auth</c> requires them even on loopback); stream names are
/// opaque ids and sources must be <c>rtsp://</c> / <c>rtsps://</c> (never <c>exec:</c> or similar). Neither a source nor
/// a go2rtc answer body ever appears in an exception or a log; the source's credentials are registered with the log
/// redactor before it is sent.
/// </summary>
public sealed partial class Go2RtcRestreamer : ICameraRestreamer
{
    /// <summary>The longest answer read from go2rtc (an SDP answer is a few KB).</summary>
    public const int MaxAnswerBytes = 256 * 1024;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly Func<HttpClient> _clients;
    private readonly KnownSecrets _secrets;
    private readonly ILogger<Go2RtcRestreamer> _logger;
    private readonly Uri? _base;
    private readonly string? _authorization;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _time;

    /// <param name="clients">Creates the HTTP client for one call (DI: the named <c>go2rtc</c> client, without request logging).</param>
    public Go2RtcRestreamer(IOptions<Go2RtcOptions> options, Func<HttpClient> clients, KnownSecrets secrets, TimeProvider time, ILogger<Go2RtcRestreamer> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _clients = clients;
        _secrets = secrets;
        _time = time;
        _logger = logger;
        var settings = options.Value;
        _timeout = settings.RequestTimeout > TimeSpan.Zero ? settings.RequestTimeout : TimeSpan.FromSeconds(10);
        secrets.Register(settings.Password);

        if (string.IsNullOrWhiteSpace(settings.Url))
        {
            return;
        }

        if (!Uri.TryCreate(settings.Url.Trim(), UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
        {
            LogInvalidUrl(_logger);
            return;
        }

        if (string.IsNullOrEmpty(settings.Username) || string.IsNullOrEmpty(settings.Password))
        {
            LogMissingCredentials(_logger);
            return;
        }

        _base = new Uri(url.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
        _authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{settings.Password}"));
        if (!url.IsLoopback)
        {
            LogNotLoopback(_logger, url.Host);
        }
    }

    public bool Enabled => _base is not null;

    public async Task RegisterAsync(string streamName, CameraStreamSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var baseUri = Base();
        CheckName(streamName);
        RegisterSecrets(source.Url);
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            new Uri(baseUri, $"api/streams?name={Uri.EscapeDataString(streamName)}&src={Uri.EscapeDataString(source.Url)}"));
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new CameraRestreamerException($"go2rtc refused the stream (HTTP {(int)response.StatusCode}).");
        }
    }

    public async Task<string> ExchangeWebRtcAsync(string streamName, string offerSdp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offerSdp);
        var baseUri = Base();
        CheckName(streamName);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, $"api/webrtc?src={Uri.EscapeDataString(streamName)}"))
        {
            Content = JsonContent.Create(new SessionDescription("offer", offerSdp), options: _json),
        };
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new CameraRestreamerException($"go2rtc refused the WebRTC offer (HTTP {(int)response.StatusCode}).");
        }

        try
        {
            var body = await ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            var answer = JsonSerializer.Deserialize<SessionDescription>(body, _json);
            return answer is { Type: "answer", Sdp: { Length: > 0 } sdp } && sdp.StartsWith("v=0", StringComparison.Ordinal)
                ? sdp
                : throw new CameraRestreamerException("go2rtc did not answer with an SDP answer.");
        }
        catch (JsonException ex)
        {
            throw new CameraRestreamerException("go2rtc did not answer with an SDP answer.", ex);
        }
    }

    public async Task<WebSocket> ConnectAsync(string streamName, CancellationToken cancellationToken)
    {
        var baseUri = Base();
        CheckName(streamName);
        var builder = new UriBuilder(new Uri(baseUri, $"api/ws?src={Uri.EscapeDataString(streamName)}"))
        {
            Scheme = baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        };
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", _authorization);
        socket.Options.Proxy = null;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        using var timer = new CancellationTokenSource(_timeout, _time);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        try
        {
            await socket.ConnectAsync(builder.Uri, timeout.Token).ConfigureAwait(false);
            return socket;
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            socket.Dispose();
            throw new CameraRestreamerException($"go2rtc's stream socket cannot be opened ({ex.GetType().Name}).", ex);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private Uri Base() => _base ?? throw new CameraRestreamerException("No camera restreamer is configured.");

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation("Authorization", _authorization);
        using var timer = new CancellationTokenSource(_timeout, _time);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        var client = _clients();
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CameraRestreamerException("go2rtc did not answer in time.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new CameraRestreamerException($"go2rtc cannot be reached ({ex.HttpRequestError}).", ex);
        }
    }

    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxAnswerBytes)
        {
            throw new CameraRestreamerException("go2rtc's answer is too large.");
        }

        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var memory = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (memory.Length + read > MaxAnswerBytes)
                {
                    throw new CameraRestreamerException("go2rtc's answer is too large.");
                }

                memory.Write(buffer, 0, read);
            }

            return Encoding.UTF8.GetString(memory.ToArray());
        }
    }

    /// <summary>Masks the source in every log line from now on: the whole URL and its password, raw and escaped.</summary>
    private void RegisterSecrets(string url)
    {
        _secrets.Register(url);
        _secrets.Register(Uri.EscapeDataString(url));
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.UserInfo.IndexOf(':', StringComparison.Ordinal) is var colon and >= 0)
        {
            var escaped = uri.UserInfo[(colon + 1)..];
            _secrets.Register(escaped);
            _secrets.Register(Uri.UnescapeDataString(escaped));
        }
    }

    private static void CheckName(string streamName)
    {
        if (!StreamName().IsMatch(streamName ?? string.Empty))
        {
            throw new ArgumentException("A stream name is an opaque id of letters, digits, '_' and '-'.", nameof(streamName));
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex StreamName();

    [LoggerMessage(Level = LogLevel.Error, Message = "Cameras:Go2Rtc:Url is not an absolute http(s) URL without credentials, query or fragment: live view is off")]
    private static partial void LogInvalidUrl(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cameras:Go2Rtc:Username and Cameras:Go2Rtc:Password must both be set (random values): live view is off")]
    private static partial void LogMissingCredentials(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "go2rtc is reached at {Host}, not on loopback: make sure its API port is not published to the LAN")]
    private static partial void LogNotLoopback(ILogger logger, string host);

    private sealed record SessionDescription(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("sdp")] string Sdp);
}

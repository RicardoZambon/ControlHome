using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ControlHome.Connectors.DahuaHttp.Model;

namespace ControlHome.Connectors.DahuaHttp.Protocol;

/// <summary>Where and as whom a client talks to one device.</summary>
/// <param name="BaseUri"><c>http://host:port/</c>.</param>
/// <param name="Password">The device password (secret), or <c>null</c> to send no credentials.</param>
/// <param name="Time">The clock of every timeout (the runtime's or the flow's).</param>
internal sealed record DahuaConnection(Uri BaseUri, string Username, string? Password, TimeProvider Time)
{
    /// <summary>Never the password.</summary>
    public override string ToString() => $"DahuaConnection({BaseUri.Authority}, {Username})";
}

/// <summary>Creates the client of one device (tests substitute the handlers' timing).</summary>
internal delegate DahuaHttpClient DahuaClientFactory(DahuaConnection connection);

/// <summary>Why a call to the device failed.</summary>
internal enum DahuaFailure
{
    /// <summary>HTTP 401: wrong user or password, or Digest/MD5 not accepted. Never retry quickly (device lockout).</summary>
    Unauthorized,

    /// <summary>HTTP 403: the user lacks the permission, or an IP filter blocks this server.</summary>
    Forbidden,

    /// <summary>HTTP 404: the CGI API (or this CGI) is disabled or missing on this firmware.</summary>
    NotFound,

    /// <summary>HTTP 400: the firmware does not support the request.</summary>
    BadRequest,

    /// <summary>HTTP 5xx: often the authentication mode (Compatibility + MD5 needed) or a firmware fault.</summary>
    ServerError,

    /// <summary>Any other unexpected status.</summary>
    OtherStatus,

    /// <summary>No connection (refused, no route, DNS).</summary>
    Unreachable,

    /// <summary>The device did not answer in time.</summary>
    Timeout,

    /// <summary>
    /// A one-shot command was sent, but the connection broke or ended before the answer: the device may have carried it
    /// out (never sent again).
    /// </summary>
    NoAnswer,

    /// <summary>The device answered something this connector cannot use.</summary>
    InvalidResponse,
}

/// <summary>A failed call to a Dahua-family device. The message never contains credentials or URLs.</summary>
internal sealed class DahuaHttpException(DahuaFailure failure, HttpStatusCode? status, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public DahuaFailure Failure { get; } = failure;

    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// The CGI API of one Dahua-family device (Intelbras VIP cameras, NVD/MHDX recorders, SS access terminals) over HTTP with
/// Digest authentication: <see cref="SocketsHttpHandler"/> with a <see cref="CredentialCache"/> holding the credentials
/// for <c>Digest</c> only (never Basic), no proxy, no redirects, no cookies. Every call is made once; retry policy belongs
/// to the caller (and a 401 is never retried quickly: devices lock out after a few failed logins).
/// <para>
/// Door releases (and the XPE relay) never go through <see cref="SocketsHttpHandler"/>: it silently re-sends a request
/// without a body (up to three times) when the server closes the connection before answering. They are sent with
/// <see cref="OneShotHttp"/> instead — the Digest challenge on one connection, then the authenticated request exactly once
/// on a new one (<see cref="DigestChallenge"/>), each with <c>Connection: close</c> (at-most-once).
/// </para>
/// </summary>
internal sealed class DahuaHttpClient : IDisposable
{
    /// <summary>The largest text answer read (config tables of big recorders are a few tens of KB).</summary>
    public const int MaxTextBytes = 512 * 1024;

    private const string EventStreamPath = "cgi-bin/eventManager.cgi?action=attach&codes=[All]&heartbeat=5";

    /// <summary>The XPE relay's query parameter carrying the device password.</summary>
    private const string XpePasswordParameter = "Password";

    private readonly DahuaConnection _connection;
    private readonly DahuaRuntimeOptions _options;
    private readonly HttpClient _client;
    private readonly HttpClient _plain;

    internal DahuaHttpClient(DahuaConnection connection, DahuaRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);
        _connection = connection;
        _options = options;
        _client = new HttpClient(CreateHandler(connection, options, reuseConnections: true, credentials: true), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _plain = new HttpClient(CreateHandler(connection, options, reuseConnections: false, credentials: false), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>"host:port", for messages (never credentials).</summary>
    public string Endpoint => _connection.BaseUri.Authority;

    public static DahuaClientFactory CreateFactory(DahuaRuntimeOptions options) => connection => new DahuaHttpClient(connection, options);

    /// <summary>Model, firmware, serial, name and vendor (<c>magicBox.cgi</c>); the first two are required.</summary>
    public async Task<DahuaIdentity> IdentifyAsync(CancellationToken cancellationToken)
    {
        var type = (await GetAsync("cgi-bin/magicBox.cgi?action=getDeviceType", cancellationToken).ConfigureAwait(false))["type"];
        if (string.IsNullOrWhiteSpace(type))
        {
            throw new DahuaHttpException(DahuaFailure.InvalidResponse, HttpStatusCode.OK, $"The device at {Endpoint} did not report its type.");
        }

        var version = (await GetAsync("cgi-bin/magicBox.cgi?action=getSoftwareVersion", cancellationToken).ConfigureAwait(false))["version"];
        var serial = (await GetOptionalAsync("cgi-bin/magicBox.cgi?action=getSerialNo", cancellationToken).ConfigureAwait(false))?["sn"];
        var name = (await GetOptionalAsync("cgi-bin/magicBox.cgi?action=getMachineName", cancellationToken).ConfigureAwait(false))?["name"];
        var vendor = (await GetOptionalAsync("cgi-bin/magicBox.cgi?action=getVendor", cancellationToken).ConfigureAwait(false))?["vendor"];
        return new DahuaIdentity(type.Trim(), Blank(version), Blank(serial), Blank(name), Blank(vendor));
    }

    /// <summary>The video channels' titles by 1-based channel (empty when the firmware has no <c>ChannelTitle</c> table).</summary>
    public async Task<SortedDictionary<int, string>> GetChannelTitlesAsync(CancellationToken cancellationToken) =>
        (await GetOptionalAsync("cgi-bin/configManager.cgi?action=getConfig&name=ChannelTitle", cancellationToken).ConfigureAwait(false))
            ?.ChannelTitles() ?? [];

    /// <summary>The enabled smart (IVS) rule types per 1-based channel (empty when unsupported).</summary>
    public async Task<IReadOnlyDictionary<int, IReadOnlySet<string>>> GetSmartRulesAsync(CancellationToken cancellationToken) =>
        (await GetOptionalAsync("cgi-bin/configManager.cgi?action=getConfig&name=VideoAnalyseRule", cancellationToken).ConfigureAwait(false))
            ?.EnabledRuleTypes() ?? new Dictionary<int, IReadOnlySet<string>>();

    /// <summary>The number of alarm inputs (<c>alarm.cgi?action=getInSlots</c>); 0 when unsupported.</summary>
    public async Task<int> GetAlarmInputCountAsync(CancellationToken cancellationToken)
    {
        var answer = await GetOptionalAsync("cgi-bin/alarm.cgi?action=getInSlots", cancellationToken).ConfigureAwait(false);
        return int.TryParse(answer?["result"], NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? Math.Min(count, DahuaInventory.MaxAlarmInputs) : 0;
    }

    /// <summary>
    /// The 0-based channels where <paramref name="code"/> is active right now (<c>getEventIndexes</c>); firmware answers
    /// an error status when none is, which counts as none.
    /// </summary>
    public async Task<IReadOnlySet<int>> GetActiveEventChannelsAsync(string code, CancellationToken cancellationToken)
    {
        var answer = await GetOptionalAsync(
            $"cgi-bin/eventManager.cgi?action=getEventIndexes&code={Uri.EscapeDataString(code)}",
            cancellationToken).ConfigureAwait(false);
        return answer?.ChannelIndexes() ?? new HashSet<int>();
    }

    /// <summary>Whether the door is open (<c>getDoorStatus</c>), or <c>null</c> when the terminal does not say.</summary>
    public async Task<bool?> GetDoorOpenAsync(int door, CancellationToken cancellationToken)
    {
        var answer = await GetOptionalAsync(
            string.Create(CultureInfo.InvariantCulture, $"cgi-bin/accessControl.cgi?action=getDoorStatus&channel={door}"),
            cancellationToken).ConfigureAwait(false);
        return (answer?["Info.status"] ?? answer?["status"])?.ToUpperInvariant() switch
        {
            "OPEN" => true,
            "CLOSE" or "CLOSED" => false,
            _ => null,
        };
    }

    /// <summary>A still of a 1-based (or, on some firmware, 0-based) video channel (<c>snapshot.cgi</c>).</summary>
    /// <exception cref="DahuaHttpException">Not answered, or not a JPEG/PNG image.</exception>
    public async Task<(string ContentType, byte[] Data)> GetSnapshotAsync(int channel, CancellationToken cancellationToken)
    {
        using var timeout = StartDeadline(cancellationToken, _options.RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Address(string.Create(CultureInfo.InvariantCulture, $"cgi-bin/snapshot.cgi?channel={channel}")));
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            Ensure(response);
            var data = await ReadLimitedAsync(response.Content, _options.MaxSnapshotBytes, timeout.Token).ConfigureAwait(false);
            var contentType = ImageType(data)
                ?? throw new DahuaHttpException(DahuaFailure.InvalidResponse, response.StatusCode, $"The device at {Endpoint} did not answer with an image.");
            return (contentType, data);
        }
        catch (Exception exception) when (Translate(exception, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>
    /// Opens the long-lived event stream (<c>eventManager.cgi?action=attach&amp;codes=[All]&amp;heartbeat=5</c>); the
    /// caller reads it until it ends or goes quiet, and disposes it.
    /// </summary>
    public async Task<DahuaEventStream> OpenEventStreamAsync(CancellationToken cancellationToken)
    {
        using var timeout = StartDeadline(cancellationToken, _options.RequestTimeout);
        HttpResponseMessage? response = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Address(EventStreamPath));
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            Ensure(response);
            var boundary = response.Content.Headers.ContentType?.Parameters
                .FirstOrDefault(p => string.Equals(p.Name, "boundary", StringComparison.OrdinalIgnoreCase))?.Value;
            var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var events = new DahuaEventStream(response, stream, boundary, _connection.Time, _options.HeartbeatTimeout, Endpoint);
            response = null;
            return events;
        }
        catch (Exception exception) when (Translate(exception, cancellationToken) is { } translated)
        {
            throw translated;
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <summary>
    /// Releases the door of an access terminal (<c>accessControl.cgi?action=openDoor&amp;channel=N&amp;UserID=…&amp;Type=Remote</c>):
    /// the authenticated request is sent exactly once (<see cref="OneShotHttp"/>), never retried.
    /// </summary>
    /// <exception cref="DahuaHttpException">
    /// The terminal refused, could not be reached (nothing sent), or did not answer in time / dropped the connection after
    /// the request was sent (<see cref="DahuaFailure.Timeout"/>, <see cref="DahuaFailure.NoAnswer"/>: it may have opened).
    /// </exception>
    public async Task OpenDoorAsync(int door, string userId, CancellationToken cancellationToken)
    {
        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"cgi-bin/accessControl.cgi?action=openDoor&channel={door}&UserID={Uri.EscapeDataString(userId)}&Type=Remote");
        var answer = await SendOnceAsync(path, digest: true, cancellationToken).ConfigureAwait(false);
        if (!DahuaKeyValues.Parse(answer).IsOk)
        {
            throw new DahuaHttpException(DahuaFailure.InvalidResponse, HttpStatusCode.OK, $"The access terminal at {Endpoint} did not confirm the door release.");
        }
    }

    /// <summary>
    /// The XPE door station's HTTP relay (<c>/fcgi/do?action=OpenDoor&amp;UserName&amp;Password&amp;DoorNum</c>): the
    /// credentials travel in the query (the device's own scheme), so the URL is never logged. Sent exactly once.
    /// </summary>
    public async Task OpenXpeRelayAsync(int door, CancellationToken cancellationToken)
    {
        var query = new[]
        {
            ("action", "OpenDoor"),
            ("UserName", _connection.Username),
            (XpePasswordParameter, _connection.Password ?? string.Empty),
            ("DoorNum", door.ToString(CultureInfo.InvariantCulture)),
        };
        var path = "fcgi/do?" + string.Join('&', query.Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2)}"));
        await SendOnceAsync(path, digest: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether something answers HTTP at the device's address (any status; no credentials sent).</summary>
    public async Task ProbeAsync(CancellationToken cancellationToken)
    {
        using var timeout = StartDeadline(cancellationToken, _options.RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Address(string.Empty));
            using var response = await _plain.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (Translate(exception, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _plain.Dispose();
    }

    /// <summary>A text answer as key/values; failures throw <see cref="DahuaHttpException"/>.</summary>
    public async Task<DahuaKeyValues> GetAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        using var timeout = StartDeadline(cancellationToken, _options.RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Address(pathAndQuery));
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            Ensure(response);
            var bytes = await ReadLimitedAsync(response.Content, MaxTextBytes, timeout.Token).ConfigureAwait(false);
            return DahuaKeyValues.Parse(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception exception) when (Translate(exception, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>
    /// Like <see cref="GetAsync"/>, but an answer meaning "not supported here" (400, 404, 5xx, an unusable body) is
    /// <c>null</c>. Credentials and network failures still throw.
    /// </summary>
    private async Task<DahuaKeyValues?> GetOptionalAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        try
        {
            return await GetAsync(pathAndQuery, cancellationToken).ConfigureAwait(false);
        }
        catch (DahuaHttpException exception) when (exception.Failure is DahuaFailure.BadRequest or DahuaFailure.NotFound
            or DahuaFailure.ServerError or DahuaFailure.OtherStatus or DahuaFailure.InvalidResponse)
        {
            return null;
        }
    }

    /// <summary>
    /// A command that must reach the device at most once (<see cref="OneShotHttp"/>, never through the retrying handler).
    /// With <paramref name="digest"/> it is first sent without credentials: a <c>401</c> carries the Digest challenge (the
    /// device did nothing), and the authenticated request then goes out exactly once on a new connection. A device that
    /// needs no login carries the command out on the first request, which is then the only one. Never Basic.
    /// </summary>
    private async Task<string> SendOnceAsync(string pathAndQuery, bool digest, CancellationToken cancellationToken)
    {
        using var timeout = StartDeadline(cancellationToken, _options.CommandTimeout);
        var target = Address(pathAndQuery).PathAndQuery;
        try
        {
            var response = await OneShotHttp.GetAsync(_connection.BaseUri, target, null, MaxTextBytes, _options.ConnectTimeout, _connection.Time, timeout.Token)
                .ConfigureAwait(false);
            if (digest && response.Status == (int)HttpStatusCode.Unauthorized)
            {
                var challenge = DigestChallenge.Select(response.Values("WWW-Authenticate"));
                if (challenge is null || _connection.Password is null)
                {
                    throw new DahuaHttpException(
                        DahuaFailure.Unauthorized,
                        HttpStatusCode.Unauthorized,
                        $"The device at {Endpoint} offers no usable Digest authentication (401; Basic is never used).");
                }

                var authorization = challenge.Authorize(HttpMethod.Get.Method, target, _connection.Username, _connection.Password);
                response = await OneShotHttp.GetAsync(_connection.BaseUri, target, authorization, MaxTextBytes, _options.ConnectTimeout, _connection.Time, timeout.Token)
                    .ConfigureAwait(false);
            }

            Ensure((HttpStatusCode)response.Status);
            return Encoding.UTF8.GetString(response.Body);
        }
        catch (Exception exception) when (Translate(exception, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    private Uri Address(string pathAndQuery) => new(_connection.BaseUri, pathAndQuery);

    private Deadline StartDeadline(CancellationToken cancellationToken, TimeSpan timeout) => new(cancellationToken, timeout, _connection.Time);

    private void Ensure(HttpResponseMessage response) => Ensure(response.StatusCode);

    private void Ensure(HttpStatusCode status)
    {
        if ((int)status is >= 200 and < 300)
        {
            return;
        }

        var (failure, message) = (int)status switch
        {
            401 => (DahuaFailure.Unauthorized, $"The device at {Endpoint} refused the user name or password (401)."),
            403 => (DahuaFailure.Forbidden, $"The device at {Endpoint} refused access (403): missing permission or an IP filter."),
            404 => (DahuaFailure.NotFound, $"The device at {Endpoint} has no such CGI (404): the CGI API may be disabled."),
            400 => (DahuaFailure.BadRequest, $"The device at {Endpoint} does not support this request (400)."),
            >= 500 => (DahuaFailure.ServerError, $"The device at {Endpoint} answered with an internal error ({(int)status})."),
            _ => (DahuaFailure.OtherStatus, $"The device at {Endpoint} answered HTTP {(int)status}."),
        };
        throw new DahuaHttpException(failure, status, message);
    }

    private DahuaHttpException? Translate(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        DahuaHttpException => null,
        OperationCanceledException when cancellationToken.IsCancellationRequested => null,
        OperationCanceledException => new DahuaHttpException(DahuaFailure.Timeout, null, $"The device at {Endpoint} did not answer in time.", exception),
        OneShotHttpException { RequestSent: true } => new DahuaHttpException(
            DahuaFailure.NoAnswer,
            null,
            $"The device at {Endpoint} dropped the connection after the request was sent ({exception.Message}).",
            exception),
        OneShotHttpException => new DahuaHttpException(DahuaFailure.Unreachable, null, $"Cannot reach the device at {Endpoint} ({exception.Message}).", exception),
        HttpRequestException { InnerException: SocketException socket } => new DahuaHttpException(
            DahuaFailure.Unreachable,
            null,
            $"Cannot reach the device at {Endpoint} ({socket.SocketErrorCode}).",
            exception),
        HttpRequestException or IOException => new DahuaHttpException(DahuaFailure.Unreachable, null, $"The connection to the device at {Endpoint} failed.", exception),
        _ => null,
    };

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > limit)
        {
            throw new DahuaHttpException(DahuaFailure.InvalidResponse, HttpStatusCode.OK, "The device's answer is too large.");
        }

        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var memory = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (memory.Length + read > limit)
                {
                    throw new DahuaHttpException(DahuaFailure.InvalidResponse, HttpStatusCode.OK, "The device's answer is too large.");
                }

                memory.Write(buffer, 0, read);
            }

            return memory.ToArray();
        }
    }

    /// <summary><c>image/jpeg</c> or <c>image/png</c> by the data's magic bytes, else <c>null</c>.</summary>
    internal static string? ImageType(ReadOnlySpan<byte> data) =>
        data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF ? "image/jpeg"
        : data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) ? "image/png"
        : null;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static SocketsHttpHandler CreateHandler(DahuaConnection connection, DahuaRuntimeOptions options, bool reuseConnections, bool credentials)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = options.ConnectTimeout,
            MaxConnectionsPerServer = 4,
            PooledConnectionIdleTimeout = reuseConnections ? TimeSpan.FromSeconds(30) : TimeSpan.Zero,
            PooledConnectionLifetime = reuseConnections ? TimeSpan.FromMinutes(5) : TimeSpan.Zero,
        };
        if (credentials && connection.Password is { } password)
        {
            var cache = new CredentialCache
            {
                { connection.BaseUri, "Digest", new NetworkCredential(connection.Username, password) },
            };
            handler.Credentials = cache;
        }

        return handler;
    }
}

/// <summary>
/// The caller's token, cancelled also after a timeout on the given clock (a <see cref="CancellationTokenSource"/> created
/// with a <see cref="TimeProvider"/>, linked to the caller's; both disposed together).
/// </summary>
internal sealed class Deadline : IDisposable
{
    private readonly CancellationTokenSource _timer;
    private readonly CancellationTokenSource _linked;

    public Deadline(CancellationToken cancellationToken, TimeSpan timeout, TimeProvider time)
    {
        _timer = new CancellationTokenSource(timeout, time);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _timer.Token);
    }

    public CancellationToken Token => _linked.Token;

    public void Dispose()
    {
        _linked.Dispose();
        _timer.Dispose();
    }
}

/// <summary>
/// The open event stream of one device: <see cref="ReadPartsAsync"/> yields the text of each multipart part until the
/// device closes the stream (or stops sending anything — not even a heartbeat — for the heartbeat timeout).
/// </summary>
internal sealed class DahuaEventStream(
    HttpResponseMessage response,
    Stream stream,
    string? boundary,
    TimeProvider time,
    TimeSpan idleTimeout,
    string endpoint) : IAsyncDisposable
{
    /// <summary>The boundary the response declared (<c>null</c>: detected from the body).</summary>
    public string? Boundary { get; } = boundary;

    /// <exception cref="DahuaHttpException">The stream went quiet (<see cref="DahuaFailure.Timeout"/>) or broke.</exception>
    public async IAsyncEnumerable<string> ReadPartsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var parser = new MultipartEventStreamParser(Boundary);
        var buffer = new byte[8 * 1024];
        while (true)
        {
            int read;
            using (var idle = new Deadline(cancellationToken, idleTimeout, time))
            {
                try
                {
                    read = await stream.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new DahuaHttpException(DahuaFailure.Timeout, null, $"The event stream of the device at {endpoint} went quiet.", exception);
                }
                catch (Exception exception) when (exception is IOException or HttpRequestException)
                {
                    throw new DahuaHttpException(DahuaFailure.Unreachable, null, $"The event stream of the device at {endpoint} broke.", exception);
                }
            }

            if (read == 0)
            {
                foreach (var last in parser.Complete())
                {
                    yield return last;
                }

                yield break;
            }

            foreach (var part in parser.Feed(buffer.AsSpan(0, read)))
            {
                yield return part;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync().ConfigureAwait(false);
        response.Dispose();
    }
}

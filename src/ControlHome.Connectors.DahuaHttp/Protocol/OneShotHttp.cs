using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace ControlHome.Connectors.DahuaHttp.Protocol;

/// <summary>
/// One HTTP/1.1 <c>GET</c> on a new TCP connection, written and read here, for the requests that must reach a device at
/// most once (the door release, the XPE relay).
/// <para>
/// <see cref="System.Net.Http.SocketsHttpHandler"/> cannot be used for them: when the server closes a connection before
/// the first byte of its answer, it assumes an idle keep-alive connection was dropped and silently sends a request without
/// a body again — up to three more times, even on a brand-new connection. A terminal that released the door and then
/// dropped the connection (a reboot, a firmware fault) would release it again. Here nothing is ever re-sent: once the
/// request was written, a connection that breaks or ends early is <see cref="OneShotHttpException.RequestSent"/> for the
/// caller, who must treat the outcome as unknown.
/// </para>
/// </summary>
internal static class OneShotHttp
{
    /// <summary>The longest status line plus headers accepted.</summary>
    public const int MaxHeaderBytes = 16 * 1024;

    /// <summary>Sends the request once and reads the whole answer (the connection is closed afterwards).</summary>
    /// <param name="target">The request target exactly as it goes on the request line (path and query).</param>
    /// <param name="authorization">The <c>Authorization</c> header value, or <c>null</c>.</param>
    /// <param name="connectTimeout">Opening the TCP connection (on <paramref name="time"/>); nothing is sent before.</param>
    /// <exception cref="OneShotHttpException">The connection failed or the answer was unusable.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired.</exception>
    public static async Task<OneShotResponse> GetAsync(
        Uri baseUri,
        string target,
        string? authorization,
        int maxBodyBytes,
        TimeSpan connectTimeout,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentException.ThrowIfNullOrEmpty(target);
        if (!target.StartsWith('/') || target.Any(c => c is '\r' or '\n' or ' ') || authorization?.Any(c => c is '\r' or '\n') == true)
        {
            throw new ArgumentException("The request target must be an absolute path without spaces or line breaks.", nameof(target));
        }

        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using (var connectTimer = new CancellationTokenSource(connectTimeout, time))
        using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectTimer.Token))
        {
            try
            {
                // DnsSafeHost: an IPv6 literal without its brackets.
                var host = baseUri.HostNameType == UriHostNameType.IPv6 ? baseUri.DnsSafeHost : baseUri.IdnHost;
                await socket.ConnectAsync(host, baseUri.Port, connecting.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new OneShotHttpException(requestSent: false, "The connection could not be opened in time.", exception);
            }
            catch (SocketException exception)
            {
                throw new OneShotHttpException(requestSent: false, $"The connection could not be opened ({exception.SocketErrorCode}).", exception);
            }
        }

        await using var stream = new NetworkStream(socket, ownsSocket: false);
        var request = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"GET {target} HTTP/1.1\r\n")
            .Append(CultureInfo.InvariantCulture, $"Host: {baseUri.Authority}\r\n");
        if (authorization is not null)
        {
            request.Append(CultureInfo.InvariantCulture, $"Authorization: {authorization}\r\n");
        }

        request.Append("Accept: */*\r\nConnection: close\r\n\r\n");
        var bytes = Encoding.ASCII.GetBytes(request.ToString());

        // From the first byte written on, the device may act on the request: no failure below may lead to sending it again.
        try
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return await ReadResponseAsync(stream, maxBodyBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is (IOException and not OneShotHttpException) or SocketException or ObjectDisposedException)
        {
            throw new OneShotHttpException(requestSent: true, "The connection broke before the answer arrived.", exception);
        }
    }

    private static async Task<OneShotResponse> ReadResponseAsync(NetworkStream stream, int maxBodyBytes, CancellationToken cancellationToken)
    {
        var reader = new Reader(stream);
        while (true)
        {
            var statusLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new OneShotHttpException(requestSent: true, "The device closed the connection without answering.");
            var parts = statusLine.Split(' ', 3);
            if (parts.Length < 2 || !parts[0].StartsWith("HTTP/1.", StringComparison.Ordinal)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var status) || status is < 100 or > 999)
            {
                throw new OneShotHttpException(requestSent: true, "The device did not answer with HTTP.");
            }

            var headers = new List<KeyValuePair<string, string>>();
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)))
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0)
                {
                    headers.Add(new(line[..colon].Trim(), line[(colon + 1)..].Trim()));
                }
            }

            if (line is null)
            {
                throw new OneShotHttpException(requestSent: true, "The device closed the connection in the middle of its answer.");
            }

            if (status is >= 100 and < 200)
            {
                continue; // an interim answer (100 Continue): the final one follows
            }

            var response = new OneShotResponse(status, headers, []);
            var body = response.Header("Transfer-Encoding") is { } encoding && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase)
                ? await reader.ReadChunkedAsync(maxBodyBytes, cancellationToken).ConfigureAwait(false)
                : long.TryParse(response.Header("Content-Length"), NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                    ? await reader.ReadExactlyAsync(length, maxBodyBytes, cancellationToken).ConfigureAwait(false)
                    : await reader.ReadToEndAsync(maxBodyBytes, cancellationToken).ConfigureAwait(false);
            return response with { Body = body };
        }
    }

    /// <summary>Buffered reading of the answer: header lines (bounded), then the body.</summary>
    private sealed class Reader(NetworkStream stream)
    {
        private readonly byte[] _buffer = new byte[8 * 1024];
        private int _start;
        private int _end;
        private int _headerBytes;

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = new List<byte>();
            while (true)
            {
                if (_start == _end && !await FillAsync(cancellationToken).ConfigureAwait(false))
                {
                    return line.Count == 0 ? null : throw new OneShotHttpException(requestSent: true, "The device closed the connection in the middle of its answer.");
                }

                var b = _buffer[_start++];
                if (++_headerBytes > MaxHeaderBytes)
                {
                    throw new OneShotHttpException(requestSent: true, "The device's answer headers are too large.");
                }

                if (b == (byte)'\n')
                {
                    if (line.Count > 0 && line[^1] == (byte)'\r')
                    {
                        line.RemoveAt(line.Count - 1);
                    }

                    return Encoding.Latin1.GetString([.. line]);
                }

                line.Add(b);
            }
        }

        public async Task<byte[]> ReadExactlyAsync(long length, int max, CancellationToken cancellationToken)
        {
            if (length > max)
            {
                throw new OneShotHttpException(requestSent: true, "The device's answer is too large.");
            }

            var body = new byte[length];
            var filled = 0;
            while (filled < length)
            {
                if (_start == _end && !await FillAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new OneShotHttpException(requestSent: true, "The device closed the connection in the middle of its answer.");
                }

                var count = (int)Math.Min(_end - _start, length - filled);
                Array.Copy(_buffer, _start, body, filled, count);
                _start += count;
                filled += count;
            }

            return body;
        }

        public async Task<byte[]> ReadToEndAsync(int max, CancellationToken cancellationToken)
        {
            using var body = new MemoryStream();
            while (_start < _end || await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                if (body.Length + (_end - _start) > max)
                {
                    throw new OneShotHttpException(requestSent: true, "The device's answer is too large.");
                }

                body.Write(_buffer, _start, _end - _start);
                _start = _end;
            }

            return body.ToArray();
        }

        public async Task<byte[]> ReadChunkedAsync(int max, CancellationToken cancellationToken)
        {
            using var body = new MemoryStream();
            while (true)
            {
                _headerBytes = 0;
                var sizeLine = await ReadLineAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new OneShotHttpException(requestSent: true, "The device closed the connection in the middle of its answer.");
                var sizeText = sizeLine.Split(';', 2)[0].Trim();
                if (!long.TryParse(sizeText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size) || size < 0)
                {
                    throw new OneShotHttpException(requestSent: true, "The device's answer is not valid chunked encoding.");
                }

                if (size == 0)
                {
                    // Trailers (if any) up to the empty line; a device that just closes is fine too.
                    while (!string.IsNullOrEmpty(await ReadLineAsync(cancellationToken).ConfigureAwait(false)))
                    {
                    }

                    return body.ToArray();
                }

                if (body.Length + size > max)
                {
                    throw new OneShotHttpException(requestSent: true, "The device's answer is too large.");
                }

                body.Write(await ReadExactlyAsync(size, max, cancellationToken).ConfigureAwait(false));
                _headerBytes = 0;
                if (await ReadLineAsync(cancellationToken).ConfigureAwait(false) is not "")
                {
                    throw new OneShotHttpException(requestSent: true, "The device's answer is not valid chunked encoding.");
                }
            }
        }

        private async Task<bool> FillAsync(CancellationToken cancellationToken)
        {
            _start = 0;
            _end = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
            return _end > 0;
        }
    }
}

/// <summary>A device's answer to <see cref="OneShotHttp.GetAsync"/>.</summary>
internal sealed record OneShotResponse(int Status, IReadOnlyList<KeyValuePair<string, string>> Headers, byte[] Body)
{
    /// <summary>The first value of a header (case-insensitive), or <c>null</c>.</summary>
    public string? Header(string name) => Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Every value of a header (case-insensitive), in order.</summary>
    public IEnumerable<string> Values(string name) =>
        Headers.Where(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value);
}

/// <summary>A failed one-shot request; the message never contains the URL or credentials.</summary>
/// <param name="requestSent">Whether the request had been written (the device may have acted on it).</param>
internal sealed class OneShotHttpException(bool requestSent, string message, Exception? inner = null) : IOException(message, inner)
{
    public bool RequestSent { get; } = requestSent;
}

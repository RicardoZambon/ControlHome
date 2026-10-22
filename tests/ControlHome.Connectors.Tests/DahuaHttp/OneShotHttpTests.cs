using System.Net;
using System.Net.Sockets;
using System.Text;
using ControlHome.Connectors.DahuaHttp.Protocol;

namespace ControlHome.Connectors.Tests.DahuaHttp;

/// <summary>
/// The one-shot HTTP/1.1 client of door releases against a scripted server on loopback: answer framings (length,
/// chunked, close-delimited, interim 100), and failures before and after the request was written.
/// </summary>
public sealed class OneShotHttpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\nOK\r\n")]
    [InlineData("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n2\r\nOK\r\n2;x=y\r\n\r\n\r\n0\r\n\r\n")]
    [InlineData("HTTP/1.0 200 OK\r\nContent-Type: text/plain\r\n\r\nOK\r\n")]
    [InlineData("HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\nOK\r\n")]
    public async Task Answers_are_read_whatever_their_framing(string answer)
    {
        await using var server = await ScriptedServer.StartAsync(answer);

        var response = await OneShotHttp.GetAsync(server.BaseUri, "/cgi-bin/x.cgi?a=1", "Digest x", 1024, TimeSpan.FromSeconds(5), TimeProvider.System, Ct);

        response.Status.ShouldBe(200);
        Encoding.ASCII.GetString(response.Body).ShouldBe("OK\r\n");
        var request = await server.RequestAsync;
        request.ShouldStartWith("GET /cgi-bin/x.cgi?a=1 HTTP/1.1\r\n");
        request.ShouldContain($"Host: {server.BaseUri.Authority}\r\n");
        request.ShouldContain("Authorization: Digest x\r\n");
        request.ShouldContain("Connection: close\r\n");
    }

    [Theory]
    [InlineData("")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Le")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 10\r\n\r\nOK")]
    [InlineData("SSH-2.0-OpenSSH\r\n")]
    public async Task A_request_that_was_written_but_not_answered_is_marked_as_sent(string answer)
    {
        await using var server = await ScriptedServer.StartAsync(answer);

        var failure = await Should.ThrowAsync<OneShotHttpException>(() =>
            OneShotHttp.GetAsync(server.BaseUri, "/x", null, 1024, TimeSpan.FromSeconds(5), TimeProvider.System, Ct));

        failure.RequestSent.ShouldBeTrue();
        server.Connections.ShouldBe(1, "never sent again");
    }

    [Fact]
    public async Task A_connection_that_cannot_be_opened_sent_nothing()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var failure = await Should.ThrowAsync<OneShotHttpException>(() =>
            OneShotHttp.GetAsync(new Uri($"http://127.0.0.1:{port}/"), "/x", null, 1024, TimeSpan.FromSeconds(5), TimeProvider.System, Ct));

        failure.RequestSent.ShouldBeFalse();
    }

    [Fact]
    public async Task Oversized_answers_and_header_injection_are_refused()
    {
        await using (var server = await ScriptedServer.StartAsync("HTTP/1.1 200 OK\r\nContent-Length: 5000\r\n\r\n" + new string('x', 5000)))
        {
            await Should.ThrowAsync<OneShotHttpException>(() =>
                OneShotHttp.GetAsync(server.BaseUri, "/x", null, 1024, TimeSpan.FromSeconds(5), TimeProvider.System, Ct));
        }

        await Should.ThrowAsync<ArgumentException>(() =>
            OneShotHttp.GetAsync(new Uri("http://127.0.0.1:1/"), "/x HTTP/1.1\r\nX-Evil: 1", null, 1024, TimeSpan.FromSeconds(5), TimeProvider.System, Ct));
        await Should.ThrowAsync<ArgumentException>(() =>
            OneShotHttp.GetAsync(new Uri("http://127.0.0.1:1/"), "/x", "Digest a\r\nX-Evil: 1", 1024, TimeSpan.FromSeconds(5), TimeProvider.System, Ct));
    }

    /// <summary>Accepts connections on loopback; each reads one request's headers, writes the canned bytes and closes.</summary>
    private sealed class ScriptedServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource<string> _request = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _stop = new();
        private readonly string _answer;
        private Task _accepting = Task.CompletedTask;
        private int _connections;

        private ScriptedServer(string answer) => _answer = answer;

        public Uri BaseUri => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");

        public Task<string> RequestAsync => _request.Task;

        public int Connections => Volatile.Read(ref _connections);

        public static Task<ScriptedServer> StartAsync(string answer)
        {
            var server = new ScriptedServer(answer);
            server._listener.Start();
            server._accepting = server.AcceptAsync();
            return Task.FromResult(server);
        }

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
                using var connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                Interlocked.Increment(ref _connections);
                var stream = connection.GetStream();
                var received = new StringBuilder();
                var buffer = new byte[4096];
                while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer, _stop.Token);
                    if (read == 0)
                    {
                        break;
                    }

                    received.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                _request.TrySetResult(received.ToString());
                await stream.WriteAsync(Encoding.ASCII.GetBytes(_answer), _stop.Token);
                connection.Client.Shutdown(SocketShutdown.Send);
            }
        }
    }
}

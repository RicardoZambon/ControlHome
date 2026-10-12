using System.Globalization;
using System.Net;
using System.Net.Sockets;
using ControlHome.Api.Hosting;

namespace ControlHome.Api.IntegrationTests;

/// <summary>The container HEALTHCHECK (<c>dotnet ControlHome.Api.dll --healthcheck</c>); no SQL Server needed.</summary>
public sealed class HealthcheckCommandTests
{
    private static readonly Uri _liveUri = new("http://127.0.0.1:8711/health/live");

    [Theory]
    [InlineData(new[] { "--healthcheck" }, true)]
    [InlineData(new[] { "--urls", "http://+:1", "--healthcheck" }, true)]
    [InlineData(new[] { "--urls", "http://+:1" }, false)]
    [InlineData(new[] { "--HEALTHCHECK" }, false)]
    [InlineData(new[] { "healthcheck" }, false)]
    public void IsRequested_only_for_the_exact_argument(string[] args, bool expected) =>
        HealthcheckCommand.IsRequested(args).ShouldBe(expected);

    [Fact]
    public void IsRequested_is_false_without_arguments() => HealthcheckCommand.IsRequested([]).ShouldBeFalse();

    [Theory]
    [InlineData(null, 8080)]
    [InlineData("", 8080)]
    [InlineData("   ", 8080)]
    [InlineData("8711", 8711)]
    [InlineData(" 8711 ", 8711)]
    [InlineData("8711;8080", 8711)]
    [InlineData(";9000;8080", 9000)]
    [InlineData("1", 1)]
    [InlineData("65535", 65535)]
    [InlineData("0", 8080)]
    [InlineData("65536", 8080)]
    [InlineData("-1", 8080)]
    [InlineData("+8711", 8080)]
    [InlineData("http", 8080)]
    [InlineData("*:8711", 8080)]
    public void ResolvePort_uses_the_first_ASPNETCORE_HTTP_PORTS_entry_or_8080(string? httpPorts, int expected) =>
        HealthcheckCommand.ResolvePort(httpPorts).ShouldBe(expected);

    [Theory]
    [InlineData(null, "http://127.0.0.1:8080/health/live")]
    [InlineData("8711", "http://127.0.0.1:8711/health/live")]
    [InlineData("8711;8443", "http://127.0.0.1:8711/health/live")]
    public void ResolveUri_targets_loopback_health_live(string? httpPorts, string expected) =>
        HealthcheckCommand.ResolveUri(httpPorts).ShouldBe(new Uri(expected));

    [Theory]
    [InlineData(HttpStatusCode.OK, 0)]
    [InlineData(HttpStatusCode.NoContent, 0)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 1)]
    [InlineData(HttpStatusCode.NotFound, 1)]
    [InlineData(HttpStatusCode.InternalServerError, 1)]
    public async Task ProbeAsync_returns_0_only_for_a_success_status(HttpStatusCode status, int expectedExitCode)
    {
        using var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(status)));
        using var output = new StringWriter();

        var exitCode = await HealthcheckCommand.ProbeAsync(_liveUri, TimeSpan.FromSeconds(2), handler, output);

        exitCode.ShouldBe(expectedExitCode);
        handler.RequestedUri.ShouldBe(_liveUri);
        handler.RequestedMethod.ShouldBe(HttpMethod.Get);
        output.ToString().ShouldContain(((int)status).ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ProbeAsync_returns_1_when_the_connection_fails()
    {
        using var handler = new StubHandler(_ => throw new HttpRequestException("Connection refused"));
        using var output = new StringWriter();

        var exitCode = await HealthcheckCommand.ProbeAsync(_liveUri, TimeSpan.FromSeconds(2), handler, output);

        exitCode.ShouldBe(1);
        output.ToString().ShouldContain("Connection refused");
    }

    [Fact]
    public async Task ProbeAsync_returns_1_when_the_app_does_not_answer_within_the_timeout()
    {
        using var handler = new StubHandler(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var output = new StringWriter();
        var started = TimeProvider.System.GetTimestamp();

        var exitCode = await HealthcheckCommand.ProbeAsync(_liveUri, TimeSpan.FromMilliseconds(200), handler, output);

        exitCode.ShouldBe(1);
        TimeProvider.System.GetElapsedTime(started).ShouldBeLessThan(TimeSpan.FromSeconds(5));
        output.ToString().ShouldContain("timed out");
    }

    [Fact]
    public async Task ProbeAsync_returns_1_quickly_when_nothing_listens_on_the_port()
    {
        // A loopback port that was just free: bind to port 0, read the port, release it.
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
        }

        using var output = new StringWriter();
        var started = TimeProvider.System.GetTimestamp();

        var exitCode = await HealthcheckCommand.ProbeAsync(
            HealthcheckCommand.ResolveUri(port.ToString(CultureInfo.InvariantCulture)),
            HealthcheckCommand.Timeout,
            handler: null,
            output);

        exitCode.ShouldBe(1);
        TimeProvider.System.GetElapsedTime(started).ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    private sealed class StubHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public Uri? RequestedUri { get; private set; }

        public HttpMethod? RequestedMethod { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUri = request.RequestUri;
            RequestedMethod = request.Method;
            return respond(cancellationToken);
        }
    }
}

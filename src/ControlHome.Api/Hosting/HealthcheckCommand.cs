using System.Globalization;

namespace ControlHome.Api.Hosting;

/// <summary>
/// <c>dotnet ControlHome.Api.dll --healthcheck</c>: the container <c>HEALTHCHECK</c> (exec form — the chiseled runtime image
/// has no shell and no curl). Sends <c>GET http://127.0.0.1:{port}/health/live</c> with a 2 s timeout, where the port is the
/// first entry of <c>ASPNETCORE_HTTP_PORTS</c> (default 8080), and turns the answer into the process exit code: 0 for a
/// success status, 1 for anything else. Program.cs runs it before the host is built, so it reads no configuration and never
/// touches the database or the data folder.
/// </summary>
internal static class HealthcheckCommand
{
    public const string Argument = "--healthcheck";
    public const string HttpPortsVariable = "ASPNETCORE_HTTP_PORTS";

    /// <summary>Kestrel's container default when <c>ASPNETCORE_HTTP_PORTS</c> is not set.</summary>
    public const int DefaultPort = 8080;

    public const string LivePath = "/health/live";
    public const int Healthy = 0;
    public const int Unhealthy = 1;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>True when the process was started with <c>--healthcheck</c> (exact, case-sensitive).</summary>
    public static bool IsRequested(IReadOnlyCollection<string> args) =>
        args is not null && args.Contains(Argument, StringComparer.Ordinal);

    /// <summary>Probes the running app on loopback; returns the process exit code (0 healthy, 1 unhealthy).</summary>
    /// <param name="args">The process arguments; nothing besides <c>--healthcheck</c> is read from them.</param>
    public static Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var uri = ResolveUri(Environment.GetEnvironmentVariable(HttpPortsVariable));
        return ProbeAsync(uri, Timeout, handler: null, Console.Out);
    }

    /// <summary><c>http://127.0.0.1:{port}/health/live</c> for an <c>ASPNETCORE_HTTP_PORTS</c> value.</summary>
    internal static Uri ResolveUri(string? httpPorts) =>
        new($"http://127.0.0.1:{ResolvePort(httpPorts).ToString(CultureInfo.InvariantCulture)}{LivePath}");

    /// <summary>
    /// The first port of a <c>;</c>-separated <c>ASPNETCORE_HTTP_PORTS</c> value (as Kestrel parses it), or
    /// <see cref="DefaultPort"/> when the value is missing or its first entry is not a port number.
    /// </summary>
    internal static int ResolvePort(string? httpPorts)
    {
        var first = httpPorts?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
            ? port
            : DefaultPort;
    }

    /// <summary>One GET with an overall <paramref name="timeout"/>; writes a one-line verdict (kept by Docker in the health log).</summary>
    /// <param name="handler">Test seam; <c>null</c> uses a proxy-less <see cref="SocketsHttpHandler"/>.</param>
    internal static async Task<int> ProbeAsync(Uri uri, TimeSpan timeout, HttpMessageHandler? handler, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(output);

        // Never through a proxy (HTTP_PROXY may be set for the app's outbound calls); loopback only.
        handler ??= new SocketsHttpHandler { UseProxy = false, ConnectTimeout = timeout, AllowAutoRedirect = false };
        using var client = new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
        string verdict;
        int exitCode;
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            exitCode = response.IsSuccessStatusCode ? Healthy : Unhealthy;
            verdict = string.Create(CultureInfo.InvariantCulture, $"answered {(int)response.StatusCode}");
        }
        catch (TaskCanceledException)
        {
            // HttpClient.Timeout surfaces as TaskCanceledException.
            exitCode = Unhealthy;
            verdict = string.Create(CultureInfo.InvariantCulture, $"timed out after {timeout.TotalMilliseconds} ms");
        }
        catch (HttpRequestException ex)
        {
            exitCode = Unhealthy;
            verdict = $"failed: {ex.Message}";
        }

        await output.WriteLineAsync($"{(exitCode == Healthy ? "healthy" : "unhealthy")}: GET {uri} {verdict}");
        return exitCode;
    }
}

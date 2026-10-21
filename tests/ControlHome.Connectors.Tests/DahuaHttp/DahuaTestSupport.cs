using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.DahuaHttp;
using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ControlHome.Connectors.Tests.DahuaHttp;

/// <summary>The synthetic fixtures (see Fixtures/DahuaHttp/SOURCE.md) and helpers to rebuild device streams from them.</summary>
internal static partial class DahuaFixtures
{
    public const string Boundary = "myboundary";

    public static string Text(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "DahuaHttp", name));

    /// <summary>
    /// A fixture stream as a device sends it: CRLF line ends and, with <paramref name="contentLength"/>, a
    /// <c>Content-Length</c> header per part (the body plus its trailing CRLF CRLF).
    /// </summary>
    public static byte[] Multipart(string name, bool crlf = true, bool contentLength = true)
    {
        var text = Text(name).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!crlf && !contentLength)
        {
            return Encoding.UTF8.GetBytes(text);
        }

        var newline = crlf ? "\r\n" : "\n";
        var output = new StringBuilder();
        foreach (var part in text.Split("--" + Boundary + "\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf("\n\n", StringComparison.Ordinal);
            var headers = separator < 0 ? string.Empty : part[..separator];
            var body = (separator < 0 ? part : part[(separator + 2)..]).TrimEnd('\n').Replace("\n", newline, StringComparison.Ordinal) + newline + newline;
            output.Append("--").Append(Boundary).Append(newline);
            foreach (var header in headers.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                output.Append(header).Append(newline);
            }

            if (contentLength)
            {
                output.Append("Content-Length: ").Append(Encoding.UTF8.GetByteCount(body)).Append(newline);
            }

            output.Append(newline).Append(body);
        }

        return Encoding.UTF8.GetBytes(output.ToString());
    }

    /// <summary>One part as a device sends it (CRLF, with Content-Length).</summary>
    public static byte[] Part(string body)
    {
        var content = body.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n\r\n";
        return Encoding.UTF8.GetBytes($"--{Boundary}\r\nContent-Type: text/plain\r\nContent-Length: {Encoding.UTF8.GetByteCount(content)}\r\n\r\n{content}");
    }

    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>A tiny valid JPEG header (enough for the magic-byte check).</summary>
    public static byte[] Jpeg { get; } = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0xFF, 0xD9];

    /// <summary>
    /// Checks a Digest <c>Authorization</c> header against the password: HA1 = MD5(user:realm:password), HA2 =
    /// MD5(method:uri), response = MD5(HA1:nonce:nc:cnonce:qop:HA2) (RFC 7616 with qop=auth).
    /// </summary>
    public static bool IsValidDigest(string header, string method, string username, string password)
    {
        if (!header.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var values = DigestParameter().Matches(header).ToDictionary(m => m.Groups[1].Value.ToLowerInvariant(), m => m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value);
        if (values.GetValueOrDefault("username") != username)
        {
            return false;
        }

        static string Md5(string text) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));
        var ha1 = Md5($"{username}:{values["realm"]}:{password}");
        var ha2 = Md5($"{method}:{values["uri"]}");
        var expected = values.TryGetValue("qop", out var qop)
            ? Md5($"{ha1}:{values["nonce"]}:{values["nc"]}:{values["cnonce"]}:{qop}:{ha2}")
            : Md5($"{ha1}:{values["nonce"]}:{ha2}");
        return string.Equals(values["response"], expected, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("""(\w+)=("([^"]*)"|([^,\s]*))""")]
    private static partial Regex DigestParameter();
}

/// <summary>
/// A Dahua-family device on WireMock: every request without a Digest <c>Authorization</c> header gets the 401 challenge
/// (<c>WWW-Authenticate: Digest realm=…, qop="auth", nonce=…</c>); answers are registered per CGI with
/// <see cref="Answer"/>. With <see cref="RejectCredentials"/> authenticated requests get 401 too (a wrong password).
/// </summary>
internal sealed class FakeDahuaDevice : IDisposable
{
    public const string User = "controlhome";
    public const string Password = "synthetic-Pa55:w@rd";
    public const string Realm = "Login to SYNTH00000001";

    public FakeDahuaDevice()
    {
        Server = WireMockServer.Start();
        Server.Given(Request.Create().UsingAnyMethod())
            .AtPriority(1000)
            .RespondWith(Response.Create()
                .WithStatusCode(401)
                .WithHeader("WWW-Authenticate", $"Digest realm=\"{Realm}\", qop=\"auth\", nonce=\"{Guid.NewGuid():N}\", opaque=\"\"")
                .WithBody("Error\r\nInvalid Authority!\r\n"));

        // Authenticated requests for a CGI nobody registered: what firmware without it answers.
        Server.Given(Request.Create().UsingAnyMethod().WithHeader("Authorization", "Digest *"))
            .AtPriority(500)
            .RespondWith(Response.Create().WithStatusCode(404).WithBody("Error\r\nNot Found!\r\n"));
    }

    public WireMockServer Server { get; }

    public int Port => Server.Ports[0];

    /// <summary>Every request the device received, oldest first.</summary>
    public IReadOnlyList<ILogEntry> Requests => [.. Server.LogEntries.OrderBy(e => e.RequestMessage!.DateTime)];

    /// <summary>Authenticated requests for a CGI path (e.g. <c>/cgi-bin/accessControl.cgi</c>) and action.</summary>
    public IReadOnlyList<ILogEntry> Authenticated(string path, string? action = null) =>
        [.. Requests.Where(e => e.RequestMessage!.Path == path
            && (action is null || Query(e, "action") == action)
            && e.RequestMessage!.Headers?.ContainsKey("Authorization") == true)];

    /// <summary>Every request (authenticated or not) for a CGI path and action.</summary>
    public IReadOnlyList<ILogEntry> All(string path, string? action = null) =>
        [.. Requests.Where(e => e.RequestMessage!.Path == path && (action is null || Query(e, "action") == action))];

    public static string? Query(ILogEntry entry, string name) =>
        entry.RequestMessage!.Query is { } query && query.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;

    public static string? Header(ILogEntry entry, string name) =>
        entry.RequestMessage!.Headers is { } headers && headers.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>Answers an authenticated GET of <paramref name="path"/> (with <c>action</c> and optional other params).</summary>
    public IResponseBuilder Answer(string path, string? action, params (string Name, string Value)[] parameters)
    {
        var request = Request.Create().UsingGet().WithPath(path).WithHeader("Authorization", "Digest *");
        if (action is not null)
        {
            request = request.WithParam("action", action);
        }

        foreach (var (name, value) in parameters)
        {
            request = request.WithParam(name, value);
        }

        var response = Response.Create();
        Server.Given(request).AtPriority(10).RespondWith(response);
        return response;
    }

    /// <summary>Registers the answers of a camera / recorder / access terminal to the identification calls.</summary>
    public void Identity(string type, string serial = "SYNTH00000001", string? name = "Entrada", string version = "2.800.00IB000.0.R, Build Date 2023-03-01")
    {
        Answer("/cgi-bin/magicBox.cgi", "getDeviceType").WithBody($"type={type}\r\n");
        Answer("/cgi-bin/magicBox.cgi", "getSoftwareVersion").WithBody($"version={version}\r\n");
        Answer("/cgi-bin/magicBox.cgi", "getSerialNo").WithBody($"sn={serial}\r\n");
        if (name is null)
        {
            Answer("/cgi-bin/magicBox.cgi", "getMachineName").WithStatusCode(400).WithBody("Error\r\nBad Request!\r\n");
        }
        else
        {
            Answer("/cgi-bin/magicBox.cgi", "getMachineName").WithBody($"name={name}\r\n");
        }

        Answer("/cgi-bin/magicBox.cgi", "getVendor").WithStatusCode(400).WithBody("Error\r\nBad Request!\r\n");
    }

    /// <summary>Answers every authenticated request with 401 (a wrong password); unauthenticated ones still get the challenge.</summary>
    public void RejectCredentials() =>
        Server.Given(Request.Create().UsingAnyMethod().WithHeader("Authorization", "Digest *"))
            .AtPriority(1)
            .RespondWith(Response.Create()
                .WithStatusCode(401)
                .WithHeader("WWW-Authenticate", $"Digest realm=\"{Realm}\", qop=\"auth\", nonce=\"{Guid.NewGuid():N}\", opaque=\"\"")
                .WithBody("Error\r\nInvalid Authority!\r\n"));

    /// <summary>
    /// Answers the attach stream with a finite multipart body (the device closes it after sending). With
    /// <paramref name="once"/> only the first connection gets it; later ones get no answer for a minute (the runtime's
    /// header timeout ends them), so the events are not repeated.
    /// </summary>
    public void EventStream(byte[] body, bool once = false)
    {
        var request = Request.Create().UsingGet().WithPath("/cgi-bin/eventManager.cgi").WithParam("action", "attach").WithHeader("Authorization", "Digest *");
        var response = Response.Create()
            .WithHeader("Content-Type", $"multipart/x-mixed-replace; boundary={DahuaFixtures.Boundary}")
            .WithBody(body);
        if (!once)
        {
            Server.Given(request).AtPriority(10).RespondWith(response);
            return;
        }

        Server.Given(request).AtPriority(10).InScenario("attach").WillSetStateTo("sent").RespondWith(response);
        Server.Given(request).AtPriority(10).InScenario("attach").WhenStateIs("sent")
            .RespondWith(Response.Create().WithDelay(TimeSpan.FromMinutes(1)).WithStatusCode(503));
    }

    public void Dispose() => Server.Stop();
}

/// <summary>Runtime and factory wiring for tests: fast timings, the fake device's address, a fixed password.</summary>
internal static class DahuaHarness
{
    public static DahuaRuntimeOptions FastOptions { get; } = DahuaRuntimeOptions.Default with
    {
        RequestTimeout = TimeSpan.FromSeconds(5),
        ConnectTimeout = TimeSpan.FromSeconds(2),
        CommandTimeout = TimeSpan.FromSeconds(5),
        HeartbeatTimeout = TimeSpan.FromSeconds(5),
        ReconnectInitial = TimeSpan.FromMilliseconds(50),
        ReconnectMax = TimeSpan.FromMilliseconds(200),
        PulseHold = TimeSpan.FromMilliseconds(300),
    };

    public static DahuaHttpConnectorFactory Factory(DahuaRuntimeOptions? options = null, bool xpeRelay = false) =>
        new(new DahuaHttpConnectorOptions { XpeRelay = xpeRelay }, null, options ?? FastOptions);

    public static JsonElement Config(int port, string kind = "auto", string? extra = null) =>
        DahuaFixtures.Json($$"""{"host":"127.0.0.1","port":{{port}},"username":"{{FakeDahuaDevice.User}}","kind":"{{kind}}","language":"pt-BR"{{(extra is null ? string.Empty : "," + extra)}}}""");

    public static IntegrationInstance Instance(JsonElement config, string? password = FakeDahuaDevice.Password) =>
        new(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"), "Test", config, new MemorySecretStore(password is null ? [] : new() { ["password"] = password }));

    /// <summary>Starts <paramref name="runtime"/>; disposing the handle stops it and waits for it to finish.</summary>
    public static RunningRuntime Run(IConnectorRuntime runtime, IConnectorContext context) => new(runtime, context);

    public static async Task WaitUntilAsync(Func<bool> condition, string because, int timeoutSeconds = 15)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting until {because}.");
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>A runtime's <c>RunAsync</c> running in the background until disposed.</summary>
internal sealed class RunningRuntime : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

    public RunningRuntime(IConnectorRuntime runtime, IConnectorContext context)
    {
        Runtime = runtime;
        Task = runtime.RunAsync(context, _stop.Token);
    }

    public IConnectorRuntime Runtime { get; }

    public Task Task { get; }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }

        await Runtime.DisposeAsync();
        _stop.Dispose();
    }
}

/// <summary>An in-memory secret store for one integration.</summary>
internal sealed class MemorySecretStore(Dictionary<string, string> values) : ISecretStore
{
    public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken) => ValueTask.FromResult(values.GetValueOrDefault(key));

    public ValueTask SetAsync(string key, string? value, CancellationToken cancellationToken)
    {
        if (value is null)
        {
            values.Remove(key);
        }
        else
        {
            values[key] = value;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> GetDeviceSecretAsync(string externalDeviceId, string key, CancellationToken cancellationToken) =>
        ValueTask.FromResult<string?>(null);
}

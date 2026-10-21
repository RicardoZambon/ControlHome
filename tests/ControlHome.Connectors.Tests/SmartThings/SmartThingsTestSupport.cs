using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.SmartThings;
using ControlHome.Connectors.SmartThings.Auth;
using ControlHome.Connectors.SmartThings.Mapping;
using ControlHome.Connectors.SmartThings.Model;
using ControlHome.Connectors.SmartThings.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using WireMock.Logging;
using WireMock.Server;

namespace ControlHome.Connectors.Tests.SmartThings;

/// <summary>
/// Loads the fixtures under <c>Fixtures/SmartThings</c>: Home Assistant's SmartThings test fixtures (scrubbed; see
/// SOURCE.md) and synthetic capability definitions.
/// </summary>
internal static class StFixtures
{
    public const string Light = "hue_rgbw_color_bulb";
    public const string Plug = "smart_plug";
    public const string Ac = "da_ac_rac_000001";
    public const string Washer = "da_wm_wm_000001";
    public const string Fridge = "da_ref_normal_000001";
    public const string Tv = "vd_stv_2017_k";
    public const string Lock = "yale_push_button_deadbolt_lock";
    public const string Multipurpose = "multipurpose_sensor";
    public const string Motion = "aeotec_ms6";
    public const string OfflineThermostat = "ecobee_thermostat_offline";

    public static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    public static string Text(params string[] path) => File.ReadAllText(Path.Combine([AppContext.BaseDirectory, "Fixtures", "SmartThings", .. path]));

    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>The device description (the first item of <c>devices/&lt;name&gt;.json</c>).</summary>
    public static JsonElement DeviceItem(string name) => Parse(Text("devices", name + ".json")).GetProperty("items")[0];

    public static SmartThingsDevice Device(string name) => SmartThingsDevice.Parse(DeviceItem(name))!;

    public static DeviceStatus Status(string name) => DeviceStatus.Parse(Parse(Text("device_status", name + ".json")));

    public static string DeviceId(string name) => DeviceItem(name).GetProperty("deviceId").GetString()!;

    public static CapabilityDocument Capability(string id) => CapabilityDocument.Parse(Parse(Text("capabilities", id + ".json")))!;

    /// <summary>The synthetic capability definitions, as a lookup (others: <paramref name="otherwise"/>).</summary>
    public static Func<CapabilityRef, DefinitionLookup> Definitions(DefinitionLookup? otherwise = null)
    {
        var known = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "SmartThings", "capabilities"), "*.json")
            .Select(f => CapabilityDocument.Parse(Parse(File.ReadAllText(f)))!)
            .ToDictionary(d => d.Id, StringComparer.Ordinal);
        return c => known.TryGetValue(c.Id, out var document) ? DefinitionLookup.Of(document) : otherwise ?? DefinitionLookup.Unavailable;
    }

    public static MapContext Context(Func<CapabilityRef, DefinitionLookup>? definitions = null, IReadOnlyDictionary<string, string>? rooms = null) =>
        new(Now, rooms ?? new Dictionary<string, string>(), definitions ?? (_ => DefinitionLookup.Unavailable));

    public static MappedDevice Map(string name, Func<CapabilityRef, DefinitionLookup>? definitions = null) =>
        SmartThingsDeviceMapper.Map(Device(name), Status(name), Context(definitions))!;

    /// <summary>
    /// A <c>GET /v1/devices?includeStatus&amp;includeHealth</c> item: the device description with the status of each
    /// capability embedded (<c>components[].capabilities[].status</c>, as the Swagger spec's DeviceCapabilityReference) and
    /// <c>healthState</c>, in <paramref name="locationId"/>.
    /// </summary>
    public static JsonObject ListingItem(string name, string locationId, string health = "ONLINE", Action<JsonObject>? status = null)
    {
        var item = JsonNode.Parse(DeviceItem(name).GetRawText())!.AsObject();
        var statusDocument = JsonNode.Parse(Text("device_status", name + ".json"))!.AsObject();
        status?.Invoke(statusDocument);
        item["locationId"] = locationId;
        item["healthState"] = new JsonObject { ["state"] = health, ["lastUpdatedDate"] = "2026-10-21T11:59:00Z" };
        foreach (var component in item["components"]!.AsArray())
        {
            var id = component!["id"]!.GetValue<string>();
            foreach (var capability in component["capabilities"]!.AsArray())
            {
                var capabilityId = capability!["id"]!.GetValue<string>();
                if (statusDocument["components"]?[id]?[capabilityId] is JsonObject attributes)
                {
                    capability["status"] = attributes.DeepClone();
                }
            }
        }

        return item;
    }

    public static string Listing(IEnumerable<JsonObject> items, string? next = null) =>
        new JsonObject
        {
            ["items"] = new JsonArray([.. items.Select(i => (JsonNode)i)]),
            ["_links"] = next is null ? new JsonObject() : new JsonObject { ["next"] = new JsonObject { ["href"] = next } },
        }.ToJsonString();

    public static DeviceCommand Command(string endpoint, string capability, string command, string arguments = "{}") =>
        new(endpoint, capability, command, Parse(arguments));

    public static DeviceCommandBatch Batch(string device, string key, params DeviceCommand[] commands) => new(device, commands, key);
}

/// <summary>An in-memory secret store recording every write; <see cref="FailWrites"/> simulates a database outage.</summary>
internal sealed class MemorySecretStore(IReadOnlyDictionary<string, string>? values = null) : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(values ?? new Dictionary<string, string>(), StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string Key, string? Value)> _writes = new();

    public bool FailWrites { get; set; }

    public IReadOnlyList<(string Key, string? Value)> Writes => [.. _writes];

    public IReadOnlyDictionary<string, string> Values => new Dictionary<string, string>(_values, StringComparer.Ordinal);

    public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken) => ValueTask.FromResult(_values.GetValueOrDefault(key));

    public ValueTask SetAsync(string key, string? value, CancellationToken cancellationToken)
    {
        if (FailWrites)
        {
            throw new InvalidOperationException("The database is unavailable.");
        }

        _writes.Enqueue((key, value));
        if (value is null)
        {
            _values.TryRemove(key, out _);
        }
        else
        {
            _values[key] = value;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> GetDeviceSecretAsync(string externalDeviceId, string key, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);
}

/// <summary>Captures every log message (rendered, with exception text) for "never logs the secret" assertions.</summary>
internal sealed class LogCapture : ILogger
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyList<string> Messages => [.. _messages];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        _messages.Enqueue($"{logLevel}: {formatter(state, exception)} {exception}");
    }
}

/// <summary>A WireMock server standing in for SmartThings (API under <c>/v1/</c>, OAuth under <c>/oauth/</c>) plus the connector's options for it.</summary>
internal sealed class FakeSmartThings : IDisposable
{
    public const string ClientId = "00000000-0000-4000-8000-00000000c1d0";
    public const string ClientSecret = "synthetic-client-secret-0001";
    public const string LocationId = "00000000-0000-4000-8000-0000000010c0";

    public FakeSmartThings()
    {
        Server = WireMockServer.Start();
    }

    public WireMockServer Server { get; }

    public string Url => Server.Url!;

    public SmartThingsOptions Options(FakeTimeProvider? time = null) => SmartThingsOptions.Default with
    {
        ApiBaseUrl = new Uri(Url + "/v1/"),
        DocumentedEndpoints = new OAuthEndpoints(new Uri("https://auth.example.test/oauth/authorize"), new Uri(Url + "/oauth/token")),
        HomeAssistantEndpoints = new OAuthEndpoints(new Uri("https://auth.example.test/ha/authorize"), new Uri(Url + "/oauth/ha-token")),
        Jitter = 0,
        Random = new Random(7),
        GetRetryDelay = TimeSpan.FromMilliseconds(10),
    };

    public static HttpClient Http() => new(new SocketsHttpHandler { AllowAutoRedirect = false });

    /// <summary>The requests received for <paramref name="path"/> (optionally only one method).</summary>
    public IReadOnlyList<ILogEntry> Requests(string path, string? method = null) =>
        [.. Server.LogEntries.Where(e => e.RequestMessage?.Path == path && (method is null || string.Equals(e.RequestMessage?.Method, method, StringComparison.OrdinalIgnoreCase)))];

    public void Dispose() => Server.Stop();
}

/// <summary>
/// Runs a <see cref="SmartThingsRuntime"/> against <see cref="FakeSmartThings"/>, a <see cref="FakeTimeProvider"/> and a
/// <see cref="RecordingContext"/>. The runtime works on its own loop, so assertions poll (<see cref="WaitUntilAsync"/>).
/// </summary>
internal sealed class StHarness : IAsyncDisposable
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(15);
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;

    public StHarness(FakeSmartThings fake, MemorySecretStore secrets, string configJson, Func<SmartThingsOptions, SmartThingsOptions>? configure = null, ILogger? logger = null)
    {
        Fake = fake;
        Time = new FakeTimeProvider(StFixtures.Now);
        Context = new RecordingContext(Time, logger);
        Secrets = secrets;
        var options = fake.Options(Time);
        Options = configure?.Invoke(options) ?? options;
        Factory = new SmartThingsConnectorFactory(FakeSmartThings.Http, null, Options);
        Runtime = (SmartThingsRuntime)Factory.CreateRuntime(new IntegrationInstance(Context.IntegrationId, "SmartThings", StFixtures.Parse(configJson), secrets));
    }

    public FakeSmartThings Fake { get; }

    public FakeTimeProvider Time { get; }

    public RecordingContext Context { get; }

    public MemorySecretStore Secrets { get; }

    public SmartThingsOptions Options { get; }

    public SmartThingsConnectorFactory Factory { get; }

    public SmartThingsRuntime Runtime { get; }

    public static string OAuthConfig(int? pollSeconds = null)
    {
        var config = new JsonObject
        {
            ["mode"] = "oauth",
            ["clientId"] = FakeSmartThings.ClientId,
            ["redirectUri"] = "https://httpbin.org/get",
            ["endpoints"] = "documented",
            ["locationId"] = FakeSmartThings.LocationId,
            ["locationName"] = "Home",
        };
        if (pollSeconds is { } seconds)
        {
            config["pollIntervalSeconds"] = seconds;
        }

        return config.ToJsonString();
    }

    public static string PatConfig() => new JsonObject
    {
        ["mode"] = "pat",
        ["locationId"] = FakeSmartThings.LocationId,
        ["locationName"] = "Home",
        ["patIssuedAtUtc"] = "2026-10-21T08:00:00.0000000+00:00",
    }.ToJsonString();

    /// <summary>Secrets of an authorized OAuth integration whose access token is valid for <paramref name="validFor"/>.</summary>
    public static MemorySecretStore OAuthSecrets(TimeSpan validFor, string access = "access-token-0001", string refresh = "refresh-token-0001", TimeSpan? age = null)
    {
        var tokens = new TokenSet(access, refresh, StFixtures.Now + validFor, StFixtures.Now - (age ?? TimeSpan.FromHours(1)), "r:devices:*", null);
        return new MemorySecretStore(new Dictionary<string, string>(tokens.ToSecrets()) { [SecretKeys.ClientSecret] = FakeSmartThings.ClientSecret });
    }

    public void Start() => _run = Runtime.RunAsync(Context, _stop.Token);

    public void Advance(TimeSpan by) => Time.Advance(by);

    public async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (_run is { IsFaulted: true })
            {
                await _run;
            }

            if (watch.Elapsed > _timeout)
            {
                throw new TimeoutException($"Timed out waiting until {what}.");
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Lets the runtime's loop settle (no condition to wait for: e.g. "nothing more happens").</summary>
    public static Task SettleAsync() => Task.Delay(300, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_run is not null)
        {
            try
            {
                await _run;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await Runtime.DisposeAsync();
        _stop.Dispose();
    }
}

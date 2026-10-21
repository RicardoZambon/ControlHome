using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.LgThinQ;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using Microsoft.Extensions.Time.Testing;

namespace ControlHome.Connectors.Tests.LgThinQ;

/// <summary>
/// Runs an <see cref="LgThinQRuntime"/> against <see cref="FakeLgCloud"/> (WireMock REST), a <see cref="FakeLgBroker"/> and a
/// <see cref="FakeTimeProvider"/> (12:00 UTC = 09:00 in São Paulo); the energy run is fixed at 03:00 local.
/// </summary>
internal sealed class LgHarness : IAsyncDisposable
{
    public const string Pat = "test-pat-not-a-real-token";
    public const string ClientId = "controlhome-00000000-0000-4000-8000-0000000000c1";

    public static readonly DateTimeOffset Start = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly CancellationTokenSource _stop = new();

    private LgHarness(FakeLgCloud cloud, FakeLgBroker broker, LgThinQConnectorFactory factory, MemorySecretStore secrets, FakeTimeProvider time, string config)
    {
        Cloud = cloud;
        Broker = broker;
        Factory = factory;
        Secrets = secrets;
        Time = time;
        Context = new RecordingContext(time);
        Runtime = (LgThinQRuntime)factory.CreateRuntime(new IntegrationInstance(IntegrationId, "LG ThinQ", LgFixtures.Parse(config), secrets));
    }

    public static Guid IntegrationId { get; } = Guid.Parse("00000000-0000-4000-8000-0000000000e1");

    public FakeLgCloud Cloud { get; }

    public FakeLgBroker Broker { get; }

    public LgThinQConnectorFactory Factory { get; }

    public MemorySecretStore Secrets { get; }

    public FakeTimeProvider Time { get; }

    public RecordingContext Context { get; }

    public LgThinQRuntime Runtime { get; }

    public Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>Options for the runtime tests: the cloud's (fast HTTP, a generous budget), a fixed energy time.</summary>
    public static LgRuntimeOptions Options(FakeLgCloud cloud) => cloud.Options() with { EnergyTimeOfDay = TimeSpan.FromHours(3) };

    public static async Task<LgHarness> StartAsync(
        FakeLgCloud cloud,
        FakeLgBroker? broker = null,
        LgThinQConnectorFactory? factory = null,
        MemorySecretStore? secrets = null,
        FakeTimeProvider? time = null,
        string config = """{"country":"BR"}""",
        bool waitForMqtt = true)
    {
        broker ??= new FakeLgBroker();
        factory ??= new LgThinQConnectorFactory(null, Options(cloud), broker.Factory);
        secrets ??= new MemorySecretStore((LgSecrets.Pat, Pat), (LgSecrets.ClientId, ClientId));
        var harness = new LgHarness(cloud, broker, factory, secrets, time ?? new FakeTimeProvider(Start), config);
        harness.Running = Task.Run(() => harness.Runtime.RunAsync(harness.Context, harness._stop.Token));
        if (waitForMqtt)
        {
            await harness.WaitUntilAsync(() => harness.Runtime.MqttConnected, "the runtime is connected to the broker");
        }

        return harness;
    }

    public Task WaitUntilAsync(Func<bool> condition, string because) => Waiting.UntilAsync(condition, because, Running);

    public StateReport? Latest(string device, string capability, string attribute, string endpoint = "main") =>
        Context.States.LastOrDefault(s => s.ExternalDeviceId == device && s.EndpointKey == endpoint && s.CapabilityId == capability && s.AttributeKey == attribute);

    public Task WaitForStateAsync(string device, string capability, string attribute, StateValue value, string endpoint = "main") =>
        WaitUntilAsync(() => Equals(Latest(device, capability, attribute, endpoint)?.Value, value), $"{device}/{endpoint}/{capability}.{attribute} = {value}");

    public Task WaitForStatusAsync(IntegrationStatus status) =>
        WaitUntilAsync(() => Context.Statuses.Count > 0 && Context.Statuses[^1].Status == status, $"the status is {status}");

    public Task<CommandOutcome> ExecuteAsync(string device, string capability, string command, string arguments = "{}", string endpoint = "main", string? key = null) =>
        Runtime.ExecuteAsync(
            new DeviceCommandBatch(device, [new DeviceCommand(endpoint, capability, command, LgFixtures.Parse(arguments))], key ?? Guid.NewGuid().ToString("N")),
            TestContext.Current.CancellationToken);

    public Task PublishAsync(string json) => Broker.PublishAsync(FakeLgCloud.Topic, json);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await Running.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }

        await Runtime.DisposeAsync();
        _stop.Dispose();
    }
}

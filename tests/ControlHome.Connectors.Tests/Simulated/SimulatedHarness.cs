using System.Text.Json;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.Simulated;
using Microsoft.Extensions.Time.Testing;

namespace ControlHome.Connectors.Tests.Simulated;

/// <summary>
/// Runs a <see cref="SimulatedRuntime"/> against a <see cref="FakeTimeProvider"/> and a <see cref="RecordingContext"/>,
/// advancing simulated time one tick at a time (waiting until each tick is processed).
/// </summary>
internal sealed class SimulatedHarness : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan _realTimeout = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource _stop = new();

    private SimulatedHarness(string configJson, SimulatedConnectorOptions? options)
    {
        Time = new FakeTimeProvider(Start);
        Context = new RecordingContext(Time);
        var factory = new SimulatedConnectorFactory(options);
        Runtime = (SimulatedRuntime)factory.CreateRuntime(
            new IntegrationInstance(Context.IntegrationId, "Simulated", Json(configJson), EmptySecretStore.Instance));
    }

    public FakeTimeProvider Time { get; }

    public RecordingContext Context { get; }

    public SimulatedRuntime Runtime { get; }

    public Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>Creates the runtime and runs it until the inventory and initial states are reported.</summary>
    public static Task<SimulatedHarness> StartAsync(string configJson = """{"preset":"basic"}""", SimulatedConnectorOptions? options = null)
    {
        var harness = new SimulatedHarness(configJson, options);

        // RecordingContext completes synchronously, so RunAsync returns at its first tick timer: everything initial is reported.
        harness.Running = harness.Runtime.RunAsync(harness.Context, harness._stop.Token);
        return Task.FromResult(harness);
    }

    /// <summary>Advances the clock by <paramref name="ticks"/> simulation ticks, one at a time.</summary>
    public async Task AdvanceTicksAsync(int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            var before = Runtime.CompletedTicks;
            Time.Advance(SimulatedRuntime.TickInterval);
            var deadline = DateTime.UtcNow + _realTimeout;
            while (Runtime.CompletedTicks == before)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("The simulation did not process the tick.");
                }

                await Task.Delay(1, TestContext.Current.CancellationToken);
            }
        }
    }

    /// <summary>Sends a batch to the device's main endpoint and advances the clock by the simulated command latency.</summary>
    public Task<CommandOutcome> ExecuteAsync(string device, string capability, string command, string? arguments = null, string? key = null) =>
        ExecuteOnAsync(device, "main", capability, command, arguments, key);

    /// <summary>Sends a batch to one endpoint and advances the clock by the simulated command latency.</summary>
    public async Task<CommandOutcome> ExecuteOnAsync(
        string device,
        string endpoint,
        string capability,
        string command,
        string? arguments = null,
        string? key = null)
    {
        var batch = new DeviceCommandBatch(
            device,
            [new DeviceCommand(endpoint, capability, command, Json(arguments ?? "{}"))],
            key ?? Guid.NewGuid().ToString("N"));
        var pending = Runtime.ExecuteAsync(batch, TestContext.Current.CancellationToken);
        if (!pending.IsCompleted)
        {
            Time.Advance(SimulatedRuntime.CommandLatency);
        }

        return await pending.WaitAsync(_realTimeout, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await Running.WaitAsync(_realTimeout);
        await Runtime.DisposeAsync();
        _stop.Dispose();
    }

    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

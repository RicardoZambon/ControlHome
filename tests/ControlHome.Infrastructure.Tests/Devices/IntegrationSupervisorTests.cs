using ControlHome.Application.Integrations;
using ControlHome.Application.Realtime;
using ControlHome.Application.Runtime;
using ControlHome.Application.Security;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using ControlHome.Infrastructure.Devices;
using ControlHome.Infrastructure.Tests.Devices.Runtime;
using ControlHome.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static ControlHome.Infrastructure.Tests.Devices.Runtime.PipelineHarness;

namespace ControlHome.Infrastructure.Tests.Devices;

/// <summary>
/// The integration supervisor on a fake clock: restart back-off and its reset, devices Unknown while a runtime is down, and
/// never two runtimes of one integration (fencing a runtime that ignores its stop, no start after StopAllAsync).
/// </summary>
public sealed class IntegrationSupervisorTests(SqlServerFixture sql)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_failing_runtime_restarts_after_a_doubling_delay_that_resets_after_a_long_run()
    {
        await using var harness = await SupervisorHarness.CreateAsync(sql);
        var longRun = new TaskCompletionSource();
        harness.Connector.Behavior = async (run, _, _, _) =>
        {
            if (run == 7)
            {
                // Runs RestartResetAfter (the test moves the clock) before it fails.
                await longRun.Task;
                throw new InvalidOperationException("boom after a long run");
            }

            throw new InvalidOperationException("boom");
        };

        await harness.Supervisor.StartAllAsync(Ct);

        // 1 s, 2 s, 4 s, then capped at RestartMaxDelay (8 s here).
        foreach (var (seconds, run) in new[] { (1, 1), (2, 2), (4, 3), (8, 4), (8, 5), (8, 6) })
        {
            await harness.WaitForErrorAsync($"retrying in {seconds} s", run);
            harness.Time.Advance(TimeSpan.FromSeconds(seconds));
            await harness.WaitForRunsAsync(run + 1);
        }

        harness.Time.Advance(harness.Options.RestartResetAfter);
        longRun.SetResult();

        // After a long run the delay starts over.
        await harness.WaitForErrorAsync("retrying in 1 s", 7);
    }

    [Fact]
    public async Task While_a_runtime_is_down_its_devices_are_unknown_and_clients_are_told()
    {
        await using var harness = await SupervisorHarness.CreateAsync(sql);
        var online = new TaskCompletionSource();
        var fail = new TaskCompletionSource();
        harness.Connector.Behavior = async (_, context, _, _) =>
        {
            context.ReportAvailability(DeviceExternalId, Availability.Online, null);
            online.SetResult();
            await fail.Task;
            throw new InvalidOperationException("connection lost");
        };

        await harness.Supervisor.StartAllAsync(Ct);
        await online.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await harness.Pipeline.SettleAsync();
        harness.Pipeline.Registry.FindDevice(harness.Pipeline.DeviceId)!.Availability.ShouldBe(Availability.Online);

        fail.SetResult();
        await harness.WaitForErrorAsync("retrying in 1 s", 1);
        await harness.Pipeline.SettleAsync();

        harness.Pipeline.Registry.FindDevice(harness.Pipeline.DeviceId)!.Availability.ShouldBe(Availability.Unknown);
        harness.Pipeline.Publisher.Availability.Select(a => a.Availability).ShouldBe([nameof(Availability.Online), nameof(Availability.Unknown)]);
    }

    [Fact]
    public async Task A_runtime_that_ignores_its_stop_is_fenced_and_its_successor_starts_only_once_it_has_finished()
    {
        await using var harness = await SupervisorHarness.CreateAsync(sql);
        var stopped = new TaskCompletionSource();
        var act = new TaskCompletionSource();
        var finish = new TaskCompletionSource();
        Exception? secretWrite = null;
        harness.Connector.Behavior = async (run, context, instance, cancellationToken) =>
        {
            if (run > 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return;
            }

            // Ignores cancellation (like an HTTP call without a token), then keeps going after its stop.
            cancellationToken.Register(() => stopped.TrySetResult());
            await act.Task;
            context.ReportState(new StateReport(DeviceExternalId, Endpoint.MainKey, CapabilityIds.Switch, "on", StateValue.True, null));
            context.SetStatus(IntegrationStatus.Ok, "zombie");
            secretWrite = await Record(() => instance.Secrets.SetAsync("refreshToken", "rotated-by-the-zombie", CancellationToken.None).AsTask());
            await finish.Task;
        };
        await harness.Supervisor.StartAllAsync(Ct);
        await harness.WaitForRunsAsync(1);

        var stopping = harness.Supervisor.StopAsync(harness.IntegrationId, Ct);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await Task.Delay(50, Ct);
        harness.Time.Advance(harness.Options.StopTimeout);
        await stopping.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        harness.Supervisor.Stopping.ShouldBe([harness.IntegrationId]);

        // Fenced: nothing the old runtime does is accepted.
        var received = harness.Pipeline.Processor.Received;
        act.SetResult();
        await RecordingPublisher.WaitUntilAsync(() => secretWrite is not null, "the zombie tried to save a secret");
        secretWrite.ShouldBeOfType<ObjectDisposedException>();
        harness.Pipeline.Processor.Received.ShouldBe(received);

        // Its successor waits for it.
        await harness.Supervisor.StartAsync(harness.IntegrationId, Ct);
        harness.Connector.Runs.ShouldBe(1);
        harness.Supervisor.Supervised.ShouldBeEmpty();

        finish.SetResult();
        await harness.WaitForRunsAsync(2);
        harness.Supervisor.Stopping.ShouldBeEmpty();
        harness.Supervisor.Supervised.ShouldBe([harness.IntegrationId]);
        harness.Pipeline.Publisher.Messages.OfType<IntegrationStatusChangedDto>().ShouldNotContain(s => s.Message == "zombie");
    }

    [Fact]
    public async Task Nothing_starts_after_StopAll_until_StartAll()
    {
        await using var harness = await SupervisorHarness.CreateAsync(sql);
        harness.Connector.Behavior = (_, _, _, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        await harness.Supervisor.StartAllAsync(Ct);
        await harness.WaitForRunsAsync(1);

        await harness.Supervisor.StopAllAsync();
        await harness.Supervisor.StartAsync(harness.IntegrationId, Ct); // e.g. queued behind the stop (lease lost)

        harness.Supervisor.Supervised.ShouldBeEmpty();
        harness.Connector.Runs.ShouldBe(1);

        await harness.Supervisor.StartAllAsync(Ct);
        await harness.WaitForRunsAsync(2);
    }

    private static async Task<Exception?> Record(Func<Task> action)
    {
        try
        {
            await action();
            return new InvalidOperationException("No exception.");
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>A supervisor over the in-memory pipeline harness (one light) and a database with one <c>scripted</c> integration.</summary>
    private sealed class SupervisorHarness : IAsyncDisposable
    {
        private SupervisorHarness(DeviceDatabase database, PipelineHarness pipeline, ScriptedConnector connector, IntegrationSupervisor supervisor)
        {
            Database = database;
            Pipeline = pipeline;
            Connector = connector;
            Supervisor = supervisor;
        }

        public DeviceDatabase Database { get; }

        public PipelineHarness Pipeline { get; }

        public ScriptedConnector Connector { get; }

        public IntegrationSupervisor Supervisor { get; }

        public Guid IntegrationId => Pipeline.IntegrationId;

        public Microsoft.Extensions.Time.Testing.FakeTimeProvider Time => Pipeline.Time;

        public DeviceRuntimeOptions Options => Pipeline.Options.Value;

        public static async Task<SupervisorHarness> CreateAsync(SqlServerFixture sql)
        {
            var database = await DeviceDatabase.CreateAsync(sql, Ct);
            var pipeline = new PipelineHarness(o =>
            {
                o.RestartInitialDelay = TimeSpan.FromSeconds(1);
                o.RestartMaxDelay = TimeSpan.FromSeconds(8);
                o.RestartResetAfter = TimeSpan.FromMinutes(5);
                o.StopTimeout = TimeSpan.FromSeconds(30);
            });
            pipeline.RuntimeState.Mode = RuntimeMode.Active;
            pipeline.Processor.Start();
            var integration = DeviceDatabase.Integration("Scripted", ScriptedConnector.Key);
            integration.Id = pipeline.IntegrationId;
            await database.WithDbAsync(async db =>
            {
                db.Integrations.Add(integration);
                await db.SaveChangesAsync(Ct);
            });

            var connector = new ScriptedConnector();
            var knownSecrets = new KnownSecrets();
            var supervisor = new IntegrationSupervisor(
                database.Services.GetRequiredService<IServiceScopeFactory>(),
                new ConnectorCatalog([connector]),
                pipeline.Processor,
                pipeline.Inventory,
                pipeline.Registry,
                pipeline.Cache,
                new IntegrationSecretProtector(new EphemeralDataProtectionProvider(), knownSecrets),
                pipeline.Publisher,
                pipeline.RuntimeState,
                pipeline.Clock,
                pipeline.Time,
                knownSecrets,
                pipeline.Options,
                NullLoggerFactory.Instance);
            return new SupervisorHarness(database, pipeline, connector, supervisor);
        }

        /// <summary>Waits until <paramref name="runs"/> runtimes were created.</summary>
        public Task WaitForRunsAsync(int runs) =>
            RecordingPublisher.WaitUntilAsync(() => Connector.Runs >= runs, $"{runs} runs started");

        /// <summary>Waits for an Error status containing <paramref name="text"/> pushed after run <paramref name="run"/> started.</summary>
        public async Task WaitForErrorAsync(string text, int run)
        {
            await RecordingPublisher.WaitUntilAsync(
                () => Pipeline.Publisher.Messages.OfType<IntegrationStatusChangedDto>()
                    .Count(s => s.Status == nameof(IntegrationStatus.Error)) >= run
                    && Pipeline.Publisher.Messages.OfType<IntegrationStatusChangedDto>()
                        .Last(s => s.Status == nameof(IntegrationStatus.Error)).Message!.Contains(text, StringComparison.Ordinal),
                $"the error status after run {run} says '{text}'");

            // The delay is armed right after the status is set; give the loop a moment to get there before time moves.
            await Task.Delay(50, Ct);
        }

        public async ValueTask DisposeAsync()
        {
            await Supervisor.DisposeAsync();
            await Pipeline.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    /// <summary>Connector <c>scripted</c>: every run calls <see cref="Behavior"/> with its number (from 1), context and instance.</summary>
    private sealed class ScriptedConnector : IConnectorFactory
    {
        public const string Key = "scripted";

        private int _runs;

        public Func<int, IConnectorContext, IntegrationInstance, CancellationToken, Task> Behavior { get; set; } =
            (_, _, _, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        /// <summary>Runtimes whose <c>RunAsync</c> started.</summary>
        public int Runs => Volatile.Read(ref _runs);

        public IConnectorDescriptor Descriptor { get; } = new ScriptedDescriptor();

        public IConnectorFlow? CreateSetupFlow() => null;

        public IConnectorRuntime CreateRuntime(IntegrationInstance instance) => new Runtime(this, instance);

        private sealed class Runtime(ScriptedConnector owner, IntegrationInstance instance) : IConnectorRuntime
        {
            public Task RunAsync(IConnectorContext context, CancellationToken cancellationToken)
            {
                var run = Interlocked.Increment(ref owner._runs);
                return owner.Behavior(run, context, instance, cancellationToken);
            }

            public Task<CommandOutcome> ExecuteAsync(DeviceCommandBatch batch, CancellationToken cancellationToken) =>
                Task.FromResult(CommandOutcome.CompletedOutcome);

            public Task RefreshAsync(string externalDeviceId, CancellationToken cancellationToken) => Task.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class ScriptedDescriptor : IConnectorDescriptor
        {
            public string Key => ScriptedConnector.Key;

            public LocalizedText DisplayName { get; } = new("Scripted", "Roteirizado");

            public string Icon => "flask-conical";

            public ConnectorCategory Category => ConnectorCategory.Virtual;

            public IReadOnlyList<FieldSchema> ConfigFields { get; } = [];

            public IReadOnlyList<FieldSchema> DeviceConfigFields { get; } = [];

            public ConnectorFeatures Features { get; } = new();
        }
    }
}

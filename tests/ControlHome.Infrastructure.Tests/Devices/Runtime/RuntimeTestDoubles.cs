using System.Collections.Concurrent;
using ControlHome.Application.Auditing;
using ControlHome.Application.Devices;
using ControlHome.Application.Realtime;
using ControlHome.Application.Runtime;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using ControlHome.Infrastructure.Devices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace ControlHome.Infrastructure.Tests.Devices.Runtime;

/// <summary>Records every message the pipeline pushes to clients (thread-safe).</summary>
internal sealed class RecordingPublisher : IRealtimePublisher
{
    private readonly ConcurrentQueue<object> _messages = new();

    public IReadOnlyList<object> Messages => [.. _messages];

    public IReadOnlyList<StateDelta> Deltas => [.. _messages.OfType<IReadOnlyList<StateDelta>>().SelectMany(d => d)];

    public IReadOnlyList<IReadOnlyList<StateDelta>> DeltaBatches => [.. _messages.OfType<IReadOnlyList<StateDelta>>()];

    public IReadOnlyList<CommandStatusDto> Statuses => [.. _messages.OfType<CommandStatusDto>()];

    public IReadOnlyList<SystemStatusDto> SystemStatuses => [.. _messages.OfType<SystemStatusDto>()];

    public IReadOnlyList<AvailabilityChangedDto> Availability => [.. _messages.OfType<AvailabilityChangedDto>()];

    public int DevicesChanged => _messages.Count(m => m is DevicesChangedMessage);

    public Task StateChangedAsync(IReadOnlyList<StateDelta> deltas, CancellationToken cancellationToken) => Add(deltas);

    public Task AvailabilityChangedAsync(AvailabilityChangedDto change, CancellationToken cancellationToken) => Add(change);

    public Task DevicesChangedAsync(CancellationToken cancellationToken) => Add(new DevicesChangedMessage());

    public Task IntegrationStatusChangedAsync(IntegrationStatusChangedDto change, CancellationToken cancellationToken) => Add(change);

    public Task CommandStatusAsync(CommandStatusDto status, CancellationToken cancellationToken) => Add(status);

    public Task SystemStatusChangedAsync(SystemStatusDto status, CancellationToken cancellationToken) => Add(status);

    /// <summary>Waits (real time) until <paramref name="condition"/> holds.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string because, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting until {because}.");
            }

            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    private Task Add(object message)
    {
        _messages.Enqueue(message);
        return Task.CompletedTask;
    }

    private sealed record DevicesChangedMessage;
}

/// <summary>An <see cref="IStateStore"/> that records batches and can be switched to fail.</summary>
internal sealed class FakeStateStore : IStateStore
{
    private readonly ConcurrentQueue<StateWriteBatch> _batches = new();

    public bool Fail { get; set; }

    /// <summary>Runs inside <see cref="WriteAsync"/> before it succeeds or fails.</summary>
    public Action? DuringWrite { get; set; }

    public IReadOnlyList<StateWriteBatch> Batches => [.. _batches];

    public int Attempts { get; private set; }

    public Task WriteAsync(StateWriteBatch batch, CancellationToken cancellationToken)
    {
        Attempts++;
        DuringWrite?.Invoke();
        if (Fail)
        {
            throw new InvalidOperationException("The test's database is down.");
        }

        _batches.Enqueue(batch);
        return Task.CompletedTask;
    }
}

/// <summary>An inventory that knows nothing (or what the test hands it) and counts lookups.</summary>
internal sealed class FakeInventory : IDeviceInventory
{
    private readonly ConcurrentQueue<AttributeAddress> _lookups = new();

    public Func<AttributeAddress, AttributeInfo?> Resolve { get; set; } = _ => null;

    public IReadOnlyList<AttributeAddress> Lookups => [.. _lookups];

    public Task UpsertAsync(Guid integrationId, DiscoveredDevice device, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RemoveAsync(Guid integrationId, string externalDeviceId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<AttributeInfo?> EnsureAttributeAsync(AttributeAddress address, CancellationToken cancellationToken)
    {
        _lookups.Enqueue(address);
        return Task.FromResult(Resolve(address));
    }
}

/// <summary>Records audit events (for the command tracker's background audit).</summary>
internal sealed class RecordingAuditLog : IAuditLog
{
    private readonly ConcurrentQueue<AuditEvent> _events = new();

    public IReadOnlyList<AuditEvent> Events => [.. _events];

    public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        _events.Enqueue(auditEvent);
        return Task.CompletedTask;
    }
}

/// <summary>A state processor over an in-memory registry with one simulated light (switch.on = 1, level.level = 2, power.power = 3).</summary>
internal sealed class PipelineHarness : IAsyncDisposable
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);

    public const int OnAttribute = 1;
    public const int LevelAttribute = 2;
    public const int PowerAttribute = 3;
    public const string DeviceExternalId = "light-1";

    public PipelineHarness(Action<DeviceRuntimeOptions>? configure = null)
    {
        var options = new DeviceRuntimeOptions { FlushInterval = TimeSpan.FromHours(1) };
        configure?.Invoke(options);
        Options = Microsoft.Extensions.Options.Options.Create(options);
        Time = new FakeTimeProvider(T0);
        Clock = new RuntimeClock(Time);
        RuntimeState = new DeviceRuntimeState(Publisher, NullLogger<DeviceRuntimeState>.Instance);
        Store = new FakeStateStore();
        WriteBehind = new StateWriteBehind(Store, RuntimeState, Time, Options, NullLogger<StateWriteBehind>.Instance);
        Batcher = new RealtimeBatcher(Publisher, Time, Options, NullLogger<RealtimeBatcher>.Instance);
        Registry.Register(Light(IntegrationId, DeviceId));
        Processor = new StateProcessor(
            Registry,
            Cache,
            WriteBehind,
            Inventory,
            Batcher,
            () => [Subscriber],
            Clock,
            Options,
            NullLogger<StateProcessor>.Instance);
    }

    public Guid IntegrationId { get; } = Guid.CreateVersion7();

    public Guid DeviceId { get; } = Guid.CreateVersion7();

    public IOptions<DeviceRuntimeOptions> Options { get; }

    public FakeTimeProvider Time { get; }

    public RuntimeClock Clock { get; }

    public RecordingPublisher Publisher { get; } = new();

    public DeviceRuntimeState RuntimeState { get; }

    public FakeStateStore Store { get; }

    public StateWriteBehind WriteBehind { get; }

    public RealtimeBatcher Batcher { get; }

    public DeviceRegistry Registry { get; } = new();

    public StateCache Cache { get; } = new();

    public FakeInventory Inventory { get; } = new();

    public RecordingSubscriber Subscriber { get; } = new();

    public StateProcessor Processor { get; }

    public StateReport Report(string capability, string attribute, StateValue value, DateTimeOffset? at = null) =>
        new(DeviceExternalId, Endpoint.MainKey, capability, attribute, value, at ?? T0);

    /// <summary>Starts the loop (if needed), waits until everything queued is processed and pushes the client batch.</summary>
    public async Task SettleAsync()
    {
        Processor.Start();
        await Processor.WhenIdleAsync(TimeSpan.FromSeconds(10));
        await Batcher.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Processor.DisposeAsync();
        Batcher.Dispose();
    }

    public static Device Light(Guid integrationId, Guid deviceId) => new()
    {
        Id = deviceId,
        IntegrationId = integrationId,
        ExternalId = DeviceExternalId,
        Name = "Light",
        DeviceClass = DeviceClasses.Light,
        Availability = Availability.Unknown,
        Endpoints =
        [
            new Endpoint
            {
                Id = Guid.CreateVersion7(),
                DeviceId = deviceId,
                Key = Endpoint.MainKey,
                Capabilities =
                [
                    new EndpointCapability { Id = 10, CapabilityId = CapabilityIds.Switch, Attributes = [new DeviceAttribute { Id = OnAttribute, Key = "on" }] },
                    new EndpointCapability { Id = 11, CapabilityId = CapabilityIds.Level, Attributes = [new DeviceAttribute { Id = LevelAttribute, Key = "level" }] },
                    new EndpointCapability { Id = 12, CapabilityId = CapabilityIds.Power, Attributes = [new DeviceAttribute { Id = PowerAttribute, Key = "power" }] },
                ],
            },
        ],
    };
}

/// <summary>Records the batches the state processor hands to subscribers.</summary>
internal sealed class RecordingSubscriber : IStateChangeSubscriber
{
    private readonly ConcurrentQueue<AttributeStateChange> _changes = new();

    public IReadOnlyList<AttributeStateChange> Changes => [.. _changes];

    public ValueTask OnStateChangedAsync(IReadOnlyList<AttributeStateChange> changes, CancellationToken cancellationToken)
    {
        foreach (var change in changes)
        {
            _changes.Enqueue(change);
        }

        return ValueTask.CompletedTask;
    }
}

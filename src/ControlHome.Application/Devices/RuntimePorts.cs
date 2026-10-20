using ControlHome.Application.Runtime;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Application.Devices;

/// <summary>One state report accepted by the state pipeline (after deduplication and out-of-order checks).</summary>
/// <param name="PreviousValue">The value before, or <c>null</c> when none was known.</param>
/// <param name="Changed">
/// Whether the value differs from the previous one (or is a momentary event). <c>false</c>: the device confirmed the value it
/// already had (useful to confirm commands); automations ignore those.
/// </param>
/// <param name="IsEvent">A momentary event (button press, doorbell): <see cref="Value"/> is the event name.</param>
public sealed record AttributeStateChange(
    int AttributeId,
    StateValue? PreviousValue,
    StateValue Value,
    DateTimeOffset ObservedAtUtc,
    bool Changed,
    bool IsEvent);

/// <summary>
/// Receives every batch of accepted state reports from the state pipeline (automation engine, command confirmations).
/// Called on the pipeline's single processing loop: return quickly (hand work to your own queue).
/// </summary>
public interface IStateChangeSubscriber
{
    ValueTask OnStateChangedAsync(IReadOnlyList<AttributeStateChange> changes, CancellationToken cancellationToken);
}

/// <summary>Delivers command batches to running connector runtimes (implemented by the integration supervisor).</summary>
public interface ICommandDispatcher
{
    /// <summary>Whether the integration's runtime is running in this process (false in standby or while it restarts).</summary>
    bool IsRunning(Guid integrationId);

    /// <summary>
    /// Calls the runtime's <c>ExecuteAsync</c> exactly once (never retried). <see cref="CommandRejectionCodes.Unavailable"/>
    /// when the runtime is not running.
    /// </summary>
    Task<CommandOutcome> DispatchAsync(Guid integrationId, DeviceCommandBatch batch, CancellationToken cancellationToken);
}

/// <summary>Starts and stops integration runtimes when integrations are created or removed (implemented by the supervisor).</summary>
public interface IIntegrationRuntimeControl
{
    /// <summary>Starts (or restarts) the integration's runtime when this process is the active runtime.</summary>
    Task StartAsync(Guid integrationId, CancellationToken cancellationToken);

    /// <summary>Stops the integration's runtime, if running, and waits for it to finish.</summary>
    Task StopAsync(Guid integrationId, CancellationToken cancellationToken);

    /// <summary>The integration was deleted: forget its devices and cached state.</summary>
    Task RemovedAsync(Guid integrationId, CancellationToken cancellationToken);
}

/// <summary>Dispatcher, runtime control and status for hosts without a device runtime: nothing runs.</summary>
public sealed class NoDeviceRuntime : ICommandDispatcher, IIntegrationRuntimeControl, IRuntimeStatus
{
    public static NoDeviceRuntime Instance { get; } = new();

    public RuntimeMode Mode => RuntimeMode.Starting;

    public bool HistoryPaused => false;

    public bool Enabled => false;

    public bool IsRunning(Guid integrationId) => false;

    public Task<CommandOutcome> DispatchAsync(Guid integrationId, DeviceCommandBatch batch, CancellationToken cancellationToken) =>
        Task.FromResult(CommandOutcome.Reject(CommandRejectionCodes.Unavailable, "No device runtime in this process."));

    public Task StartAsync(Guid integrationId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(Guid integrationId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RemovedAsync(Guid integrationId, CancellationToken cancellationToken) => Task.CompletedTask;
}

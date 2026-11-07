using ControlHome.Application.Common;
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

/// <summary>
/// The device inventory as application services use it (implemented by Infrastructure's inventory service, which also
/// keeps the runtime's device registry and state cache current).
/// </summary>
public interface IDeviceInventoryAdmin
{
    /// <summary>
    /// Adds or merges a device as if its connector had reported it (the demo seed imports a virtual connector's devices
    /// before its runtime first runs).
    /// </summary>
    /// <exception cref="ArgumentException">The device is malformed.</exception>
    Task UpsertAsync(Guid integrationId, DiscoveredDevice device, CancellationToken cancellationToken);

    /// <summary>
    /// Moves everything the user set up on <paramref name="oldDeviceId"/> to <paramref name="newDeviceId"/> — name, room,
    /// icon, endpoint names/rooms/icons/hidden/favorites (endpoints matched by key, or the only one of each), placements and
    /// history (state log, events, statistics, energy) — then deletes the old device. Errors: 404 <c>devices.not_found</c>
    /// (the old device); 400 <c>validation.failed</c> with <c>errors.newDeviceId</c> = <c>devices.not_found</c> or
    /// <c>devices.replace_same</c>.
    /// </summary>
    Task<Result> ReplaceAsync(Guid oldDeviceId, Guid newDeviceId, CancellationToken cancellationToken);
}

/// <summary>The inventory of hosts without a device runtime: nothing can be imported or replaced there.</summary>
public sealed class NoDeviceInventoryAdmin : IDeviceInventoryAdmin
{
    public static NoDeviceInventoryAdmin Instance { get; } = new();

    public Task UpsertAsync(Guid integrationId, DiscoveredDevice device, CancellationToken cancellationToken) =>
        throw new NotSupportedException("No device runtime in this process.");

    public Task<Result> ReplaceAsync(Guid oldDeviceId, Guid newDeviceId, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure(AppError.Unavailable(ErrorCodes.CommandsUnavailable, "No device runtime in this process.")));
}

/// <summary>Asks running connector runtimes for camera stills (implemented by the integration supervisor).</summary>
public interface ICameraSnapshotProvider
{
    /// <summary>
    /// The camera's still image from the integration's runtime (when it implements <see cref="ICameraSnapshotSource"/>),
    /// or <c>null</c> when it is not running, has no cameras, or has no image right now.
    /// </summary>
    Task<CameraSnapshot?> GetSnapshotAsync(Guid integrationId, string externalDeviceId, string endpointKey, CancellationToken cancellationToken);
}

/// <summary>Asks running connector runtimes where their cameras stream (implemented by the integration supervisor).</summary>
public interface ICameraStreamProvider
{
    /// <summary>
    /// The camera's stream source from the integration's runtime (when it implements <see cref="ICameraStreamSource"/>),
    /// or <c>null</c> when it is not running, has no such camera, or no stream of that quality. The source carries the
    /// camera's credentials: it goes only to the restreamer, never to a client or a log.
    /// </summary>
    Task<CameraStreamSource?> GetStreamSourceAsync(
        Guid integrationId,
        string externalDeviceId,
        string endpointKey,
        CameraStreamQuality quality,
        CancellationToken cancellationToken);
}

/// <summary>Dispatcher, runtime control and status for hosts without a device runtime: nothing runs.</summary>
public sealed class NoDeviceRuntime : ICommandDispatcher, IIntegrationRuntimeControl, IRuntimeStatus, ICameraSnapshotProvider, ICameraStreamProvider
{
    public static NoDeviceRuntime Instance { get; } = new();

    public Task<CameraSnapshot?> GetSnapshotAsync(Guid integrationId, string externalDeviceId, string endpointKey, CancellationToken cancellationToken) =>
        Task.FromResult<CameraSnapshot?>(null);

    public Task<CameraStreamSource?> GetStreamSourceAsync(
        Guid integrationId,
        string externalDeviceId,
        string endpointKey,
        CameraStreamQuality quality,
        CancellationToken cancellationToken) =>
        Task.FromResult<CameraStreamSource?>(null);

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

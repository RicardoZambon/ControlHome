using ControlHome.Domain.Devices;
using Microsoft.Extensions.Logging;

namespace ControlHome.Connectors.Abstractions;

/// <summary>
/// One running integration. The supervisor creates it with <see cref="IConnectorFactory.CreateRuntime"/>, calls
/// <see cref="RunAsync"/> (restarting it with exponential back-off when it fails) and disposes it when the integration
/// stops. <see cref="ExecuteAsync"/> and <see cref="RefreshAsync"/> are called concurrently with <see cref="RunAsync"/>
/// and only while it runs.
/// </summary>
public interface IConnectorRuntime : IAsyncDisposable
{
    /// <summary>
    /// Connects, reports the inventory (connectors with <see cref="ConnectorFeatures.PushInventory"/>), initial states and
    /// availability, then keeps reporting changes until <paramref name="cancellationToken"/> is cancelled. Returns (or
    /// throws <see cref="OperationCanceledException"/>) on cancellation; any other exception counts as a failure and the
    /// supervisor restarts the runtime after a back-off.
    /// </summary>
    Task RunAsync(IConnectorContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Executes a batch of commands for one device. The command service has already authorized and validated it
    /// (<c>CommandValidator</c>) and applied the safety policy. Delivery is at-most-once: never retry a safety-critical
    /// command, and treat a repeated <see cref="DeviceCommandBatch.IdempotencyKey"/> as a duplicate.
    /// </summary>
    /// <returns>
    /// <see cref="CommandOutcome.Accepted"/> when the device took the command and the new state will be reported,
    /// <see cref="CommandOutcome.Completed"/> when it is done, <see cref="CommandOutcome.Rejected"/> otherwise (codes in
    /// <see cref="CommandRejectionCodes"/>).
    /// </returns>
    Task<CommandOutcome> ExecuteAsync(DeviceCommandBatch batch, CancellationToken cancellationToken);

    /// <summary>Re-reads one device's state and reports it (the user asked for a refresh, or a command needs confirming).</summary>
    Task RefreshAsync(string externalDeviceId, CancellationToken cancellationToken);
}

/// <summary>
/// The host side of a running integration, handed to <see cref="IConnectorRuntime.RunAsync"/>. Report methods are
/// thread-safe, never block (they enqueue into the state pipeline) and may be called from any thread.
/// </summary>
public interface IConnectorContext
{
    Guid IntegrationId { get; }

    /// <summary>The clock: use it for every delay, timestamp and timer (tests substitute a fake one).</summary>
    TimeProvider Time { get; }

    /// <summary>A logger scoped to the integration. Never log secrets or tokens.</summary>
    ILogger Logger { get; }

    /// <summary>Reports one attribute value (in its canonical unit); unchanged and out-of-order values are dropped.</summary>
    void ReportState(StateReport report);

    /// <summary>Reports a momentary event (a button press, a doorbell ring) for an attribute with a <c>MomentarySchema</c>.</summary>
    void ReportEvent(EventReport report);

    /// <summary>Reports whether a device is reachable; <paramref name="reason"/> is a short non-secret explanation.</summary>
    void ReportAvailability(string externalDeviceId, Availability availability, string? reason);

    /// <summary>
    /// Adds or updates a device (match on the external id; vendor fields update, user fields never do). New endpoints and
    /// capabilities are added; capabilities no longer listed are retired. Completes once the inventory is saved, so states
    /// reported afterwards resolve to attribute ids.
    /// </summary>
    Task UpsertDeviceAsync(DiscoveredDevice device, CancellationToken cancellationToken);

    /// <summary>The connector no longer has the device: it becomes orphaned (kept with its name, room and history, never deleted).</summary>
    Task RemoveDeviceAsync(string externalDeviceId, CancellationToken cancellationToken);

    /// <summary>
    /// A full inventory: every device the connector has right now. Each is added or updated as with
    /// <see cref="UpsertDeviceAsync"/>, and every other device of the integration becomes orphaned as with
    /// <see cref="RemoveDeviceAsync"/> (kept, never deleted). Use it when the vendor returns the complete device list (a
    /// cloud account's devices, a bridge's device list); report partial or incremental discoveries with
    /// <see cref="UpsertDeviceAsync"/>.
    /// </summary>
    Task SyncInventoryAsync(IReadOnlyList<DiscoveredDevice> devices, CancellationToken cancellationToken);

    /// <summary>Sets the integration's status (shown to the user, pushed to clients).</summary>
    /// <param name="message">A short, non-secret explanation (e.g. the last error), or <c>null</c>.</param>
    void SetStatus(IntegrationStatus status, string? message);
}

using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Devices;
using Microsoft.Extensions.Logging;

namespace ControlHome.Infrastructure.Devices;

/// <summary>
/// The host side of one run of an integration's runtime: reports go to the <see cref="StateProcessor"/>, inventory changes
/// to the <see cref="IDeviceInventory"/>, energy windows to the <see cref="IEnergyWindowSink"/>, statuses to the
/// supervisor. Once the run is asked to stop (<see cref="Close"/>, called as soon as its stop token is cancelled, not when
/// <c>RunAsync</c> finally returns) everything the runtime still reports is ignored, so a stopped runtime can never
/// overwrite the next one's state.
/// </summary>
internal sealed class ConnectorContext(
    Guid integrationId,
    TimeProvider time,
    ILogger logger,
    StateProcessor processor,
    IDeviceInventory inventory,
    Action<IntegrationStatus, string?> setStatus,
    IEnergyWindowSink? energyWindows = null) : IConnectorContext
{
    private volatile bool _closed;

    public Guid IntegrationId { get; } = integrationId;

    public TimeProvider Time { get; } = time;

    public ILogger Logger { get; } = logger;

    public bool IsClosed => _closed;

    public void ReportState(StateReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!_closed)
        {
            processor.ReportState(IntegrationId, report);
        }
    }

    public void ReportEvent(EventReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!_closed)
        {
            processor.ReportEvent(IntegrationId, report);
        }
    }

    public void ReportAvailability(string externalDeviceId, Availability availability, string? reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(externalDeviceId);
        if (!_closed)
        {
            processor.ReportAvailability(IntegrationId, externalDeviceId, availability);
        }
    }

    public Task UpsertDeviceAsync(DiscoveredDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ObjectDisposedException.ThrowIf(_closed, this);
        return inventory.UpsertAsync(IntegrationId, device, cancellationToken);
    }

    public async Task RemoveDeviceAsync(string externalDeviceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(externalDeviceId);
        ObjectDisposedException.ThrowIf(_closed, this);
        await inventory.RemoveAsync(IntegrationId, externalDeviceId, cancellationToken).ConfigureAwait(false);

        // Through the pipeline, in the device's availability slot: an Online/Offline report still waiting is replaced, and
        // one already applied is overwritten in the write-behind before it reaches the database.
        if (!_closed)
        {
            processor.ReportAvailability(IntegrationId, externalDeviceId, Availability.Unknown);
        }
    }

    public async Task SyncInventoryAsync(IReadOnlyList<DiscoveredDevice> devices, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ObjectDisposedException.ThrowIf(_closed, this);
        var orphaned = await inventory.SyncAsync(IntegrationId, devices, cancellationToken).ConfigureAwait(false);

        // As for RemoveDeviceAsync: through the pipeline, in each orphaned device's availability slot.
        if (!_closed)
        {
            foreach (var externalDeviceId in orphaned)
            {
                processor.ReportAvailability(IntegrationId, externalDeviceId, Availability.Unknown);
            }
        }
    }

    public Task ReportEnergyWindowsAsync(IReadOnlyList<EnergyWindowReport> windows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ObjectDisposedException.ThrowIf(_closed, this);
        return energyWindows is null
            ? throw new NotSupportedException("This host does not store energy windows.")
            : energyWindows.WriteAsync(IntegrationId, windows, cancellationToken);
    }

    public void SetStatus(IntegrationStatus status, string? message)
    {
        if (!_closed)
        {
            setStatus(status, message);
        }
    }

    public void Close() => _closed = true;
}

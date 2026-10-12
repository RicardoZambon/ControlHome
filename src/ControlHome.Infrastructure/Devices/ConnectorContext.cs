using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Devices;
using Microsoft.Extensions.Logging;

namespace ControlHome.Infrastructure.Devices;

/// <summary>
/// The host side of one run of an integration's runtime: reports go to the <see cref="StateProcessor"/>, inventory changes
/// to the <see cref="IDeviceInventory"/>, statuses to the supervisor. Once the run ends (<see cref="Close"/>) everything the
/// runtime still reports is ignored, so a stopped runtime can never overwrite the next one's state.
/// </summary>
internal sealed class ConnectorContext(
    Guid integrationId,
    TimeProvider time,
    ILogger logger,
    StateProcessor processor,
    IDeviceInventory inventory,
    Action<IntegrationStatus, string?> setStatus) : IConnectorContext
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

    public Task RemoveDeviceAsync(string externalDeviceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(externalDeviceId);
        ObjectDisposedException.ThrowIf(_closed, this);
        return inventory.RemoveAsync(IntegrationId, externalDeviceId, cancellationToken);
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

using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ControlHome.Connectors.Tests;

/// <summary>An <see cref="IConnectorContext"/> that records everything a runtime reports (thread-safe).</summary>
internal sealed class RecordingContext(TimeProvider time, ILogger? logger = null) : IConnectorContext
{
    private readonly Lock _lock = new();
    private readonly List<StateReport> _states = [];
    private readonly List<EventReport> _events = [];
    private readonly List<(string Device, Availability Availability)> _availability = [];
    private readonly List<DiscoveredDevice> _devices = [];
    private readonly List<string> _removed = [];
    private readonly List<(IntegrationStatus Status, string? Message)> _statuses = [];

    public Guid IntegrationId { get; } = Guid.CreateVersion7();

    public TimeProvider Time { get; } = time;

    public ILogger Logger { get; } = logger ?? NullLogger.Instance;

    public IReadOnlyList<StateReport> States => Snapshot(_states);

    public IReadOnlyList<EventReport> Events => Snapshot(_events);

    public IReadOnlyList<(string Device, Availability Availability)> Availability => Snapshot(_availability);

    public IReadOnlyList<DiscoveredDevice> Devices => Snapshot(_devices);

    public IReadOnlyList<string> Removed => Snapshot(_removed);

    public IReadOnlyList<(IntegrationStatus Status, string? Message)> Statuses => Snapshot(_statuses);

    public void ReportState(StateReport report) => Add(_states, report);

    public void ReportEvent(EventReport report) => Add(_events, report);

    public void ReportAvailability(string externalDeviceId, Availability availability, string? reason) =>
        Add(_availability, (externalDeviceId, availability));

    public Task UpsertDeviceAsync(DiscoveredDevice device, CancellationToken cancellationToken)
    {
        Add(_devices, device);
        return Task.CompletedTask;
    }

    public Task RemoveDeviceAsync(string externalDeviceId, CancellationToken cancellationToken)
    {
        Add(_removed, externalDeviceId);
        return Task.CompletedTask;
    }

    public void SetStatus(IntegrationStatus status, string? message) => Add(_statuses, (status, message));

    /// <summary>The latest reported value of one attribute of a device's main endpoint.</summary>
    public StateReport? Latest(string device, string capability, string attribute)
    {
        lock (_lock)
        {
            return _states.LastOrDefault(s => s.ExternalDeviceId == device && s.CapabilityId == capability && s.AttributeKey == attribute);
        }
    }

    private void Add<T>(List<T> list, T item)
    {
        lock (_lock)
        {
            list.Add(item);
        }
    }

    private List<T> Snapshot<T>(List<T> list)
    {
        lock (_lock)
        {
            return [.. list];
        }
    }
}

using System.Collections.Concurrent;
using ControlHome.Api.IntegrationTests.Devices;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.Simulated;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using Microsoft.Extensions.Logging;

namespace ControlHome.Api.IntegrationTests.History;

/// <summary>
/// Connector <c>poweronly</c>: the Simulated basic home whose smart plug lost its energy meter — the plug reports power only,
/// so its energy must be integrated from power. The plug's own energy counter (and its power) as the Simulated home reported
/// them are kept in the <see cref="PowerOnlySpy"/>, to compare the integration with.
/// </summary>
public sealed class PowerOnlyConnectorFactory(PowerOnlySpy spy) : IConnectorFactory
{
    public const string Key = "poweronly";
    public const string PlugId = "plug-1";

    public IConnectorDescriptor Descriptor { get; } = new TestDescriptor(Key, SimulatedConnectorDescriptor.Instance.ConfigFields);

    public IConnectorFlow? CreateSetupFlow() => null;

    public IConnectorRuntime CreateRuntime(IntegrationInstance instance) =>
        new PowerOnlyRuntime(new SimulatedConnectorFactory().CreateRuntime(instance), spy);

    private sealed class PowerOnlyRuntime(IConnectorRuntime inner, PowerOnlySpy spy) : IConnectorRuntime
    {
        public Task RunAsync(IConnectorContext context, CancellationToken cancellationToken) =>
            inner.RunAsync(new PowerOnlyContext(context, spy), cancellationToken);

        public Task<CommandOutcome> ExecuteAsync(DeviceCommandBatch batch, CancellationToken cancellationToken) => inner.ExecuteAsync(batch, cancellationToken);

        public Task RefreshAsync(string externalDeviceId, CancellationToken cancellationToken) => inner.RefreshAsync(externalDeviceId, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class PowerOnlyContext(IConnectorContext inner, PowerOnlySpy spy) : IConnectorContext
    {
        public Guid IntegrationId => inner.IntegrationId;

        public TimeProvider Time => inner.Time;

        public ILogger Logger => inner.Logger;

        public void ReportState(StateReport report)
        {
            ArgumentNullException.ThrowIfNull(report);
            if (report.ExternalDeviceId == PlugId && report.Value.N is { } value)
            {
                if (report.CapabilityId == CapabilityIds.Energy)
                {
                    spy.Record(report.ObservedAtUtc!.Value, energyWh: value, powerW: null);
                    return;
                }

                if (report.CapabilityId == CapabilityIds.Power)
                {
                    spy.Record(report.ObservedAtUtc!.Value, energyWh: null, powerW: value);
                }
            }

            inner.ReportState(report);
        }

        public void ReportEvent(EventReport report) => inner.ReportEvent(report);

        public void ReportAvailability(string externalDeviceId, Availability availability, string? reason) =>
            inner.ReportAvailability(externalDeviceId, availability, reason);

        public Task UpsertDeviceAsync(DiscoveredDevice device, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(device);
            return inner.UpsertDeviceAsync(
                device.ExternalId != PlugId
                    ? device
                    : device with
                    {
                        Endpoints = [.. device.Endpoints.Select(e => e with { Capabilities = [.. e.Capabilities.Where(c => c.CapabilityId != CapabilityIds.Energy)] })],
                    },
                cancellationToken);
        }

        public Task RemoveDeviceAsync(string externalDeviceId, CancellationToken cancellationToken) => inner.RemoveDeviceAsync(externalDeviceId, cancellationToken);

        public void SetStatus(IntegrationStatus status, string? message) => inner.SetStatus(status, message);
    }
}

/// <summary>The Simulated plug's energy counter and power, as reported (registered as a singleton in the devices test host).</summary>
public sealed class PowerOnlySpy
{
    private readonly ConcurrentQueue<(DateTimeOffset At, double? EnergyWh, double? PowerW)> _readings = new();

    public void Clear() => _readings.Clear();

    public void Record(DateTimeOffset at, double? energyWh, double? powerW) => _readings.Enqueue((at, energyWh, powerW));

    /// <summary>Power readings observed at or after <paramref name="time"/>.</summary>
    public int PowerReadingsSince(DateTimeOffset time) => _readings.Count(r => r.PowerW is not null && r.At >= time);

    /// <summary>
    /// The plug's energy counter at <paramref name="time"/>: the last reported counter plus the power in effect since then
    /// (the Simulated plug integrates the power in effect between its ticks).
    /// </summary>
    public double EnergyAt(DateTimeOffset time)
    {
        var readings = _readings.ToList();
        var energy = readings.Where(r => r.EnergyWh is not null && r.At <= time).OrderBy(r => r.At).Last();
        var power = readings.Where(r => r.PowerW is not null && r.At <= energy.At).OrderBy(r => r.At).Last();
        return energy.EnergyWh!.Value + (power.PowerW!.Value * (time - energy.At).TotalHours);
    }
}

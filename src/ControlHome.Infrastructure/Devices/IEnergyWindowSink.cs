using ControlHome.Connectors.Abstractions;

namespace ControlHome.Infrastructure.Devices;

/// <summary>Where connector energy windows go (<see cref="IConnectorContext.ReportEnergyWindowsAsync"/>): the hourly energy history.</summary>
public interface IEnergyWindowSink
{
    /// <summary>Spreads <paramref name="windows"/> of integration <paramref name="integrationId"/> into <c>EnergyHourly</c>.</summary>
    Task WriteAsync(Guid integrationId, IReadOnlyList<EnergyWindowReport> windows, CancellationToken cancellationToken);
}

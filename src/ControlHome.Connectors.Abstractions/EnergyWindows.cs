namespace ControlHome.Connectors.Abstractions;

/// <summary>
/// The energy an endpoint used in a past time window, as a vendor cloud reports it (an LG day or month, a SmartThings
/// 15-minute delta) — for devices without a live energy counter. Reported with
/// <see cref="IConnectorContext.ReportEnergyWindowsAsync"/>; the host spreads it into the hourly energy history.
/// </summary>
/// <param name="ExternalDeviceId">The device's <see cref="DiscoveredDevice.ExternalId"/>.</param>
/// <param name="EndpointKey">The endpoint's <see cref="DiscoveredEndpoint.Key"/> the energy belongs to.</param>
/// <param name="StartUtc">The window's start (inclusive); a local midnight converted to UTC for a vendor's daily data.</param>
/// <param name="EndUtc">The window's end (exclusive), never in the future.</param>
/// <param name="Wh">The energy used in the window, in Wh (canonical unit); finite and not negative.</param>
public sealed record EnergyWindowReport(string ExternalDeviceId, string EndpointKey, DateTimeOffset StartUtc, DateTimeOffset EndUtc, double Wh)
{
    /// <summary>
    /// Whether the window is at most an hour long: measured hourly (or finer) data. Longer windows are spread evenly over
    /// their hours and flagged as estimated ("not hourly").
    /// </summary>
    public bool IsHourly => EndUtc - StartUtc <= TimeSpan.FromHours(1);
}

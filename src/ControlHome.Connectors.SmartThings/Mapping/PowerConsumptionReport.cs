using System.Text.Json;

namespace ControlHome.Connectors.SmartThings.Mapping;

/// <summary>
/// One window of a Samsung <c>powerConsumptionReport.powerConsumption</c> value: the energy used between
/// <see cref="StartUtc"/> and <see cref="EndUtc"/> (typically 15–30 min) plus the cumulative counter at its end. Energy
/// values are in Wh; <see cref="PowerW"/> in W.
/// </summary>
/// <param name="Endpoint">The endpoint (component) the report belongs to.</param>
/// <param name="DeltaWh">Energy used in the window (<c>deltaEnergy</c>).</param>
/// <param name="EnergyWh">The cumulative counter at the end of the window (<c>energy</c>).</param>
/// <param name="PowerW">The power at the end of the window (<c>power</c>; × 1000 for EHS heat pumps, as Home Assistant does).</param>
/// <param name="PowerEnergyWh">Energy computed by the device from its power readings (<c>powerEnergy</c>).</param>
/// <param name="PersistedEnergyWh">The device's persisted counter (<c>persistedEnergy</c>).</param>
/// <param name="EnergySavedWh">Energy saved by energy-saving modes (<c>energySaved</c>).</param>
internal sealed record PowerConsumptionWindow(
    string Endpoint,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    double? DeltaWh,
    double? EnergyWh,
    double? PowerW,
    double? PowerEnergyWh,
    double? PersistedEnergyWh,
    double? EnergySavedWh)
{
    /// <summary>The longest window accepted (anything longer is a vendor glitch, e.g. a <c>1970-01-01</c> start).</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(1);

    public TimeSpan Duration => EndUtc - StartUtc;
}

/// <summary>Parses <c>powerConsumptionReport.powerConsumption</c> values (pure; tested with Home Assistant's fixtures).</summary>
internal static class PowerConsumptionReport
{
    /// <summary>
    /// The window in <paramref name="value"/>, or <c>null</c> when it has no usable window (no start/end, end not after
    /// start, longer than <see cref="PowerConsumptionWindow.MaxWindow"/>, a negative delta). Values are already in Wh/W.
    /// </summary>
    /// <param name="presentationId">The device's presentation id: <c>…EHS…</c> heat pumps report power in kW.</param>
    public static PowerConsumptionWindow? Parse(JsonElement value, string endpoint, string? presentationId)
    {
        if (value.ValueKind != JsonValueKind.Object
            || Json.Time(value, "start") is not { } start
            || Json.Time(value, "end") is not { } end
            || end <= start
            || end - start > PowerConsumptionWindow.MaxWindow)
        {
            return null;
        }

        var delta = value.Num("deltaEnergy");
        if (delta is < 0)
        {
            return null;
        }

        return new PowerConsumptionWindow(
            endpoint,
            start,
            end,
            delta,
            NonNegative(value.Num("energy")),
            Power(value, presentationId),
            NonNegative(value.Num("powerEnergy")),
            NonNegative(value.Num("persistedEnergy")),
            NonNegative(value.Num("energySaved")));
    }

    /// <summary>The power in W (<c>power</c>, scaled for EHS heat pumps), or <c>null</c>.</summary>
    public static double? Power(JsonElement value, string? presentationId) =>
        value.Num("power") is { } power and >= 0
            ? IsEhs(presentationId) ? power * 1000 : power
            : null;

    /// <summary>The cumulative counter in Wh (<c>energy</c>), or <c>null</c>.</summary>
    public static double? Energy(JsonElement value) => NonNegative(value.Num("energy"));

    /// <summary>Whether <paramref name="window"/> is newer than the last window seen for its endpoint (polls repeat the same one).</summary>
    public static bool IsNew(PowerConsumptionWindow window, DateTimeOffset? lastEndUtc) => lastEndUtc is not { } last || window.EndUtc > last;

    private static bool IsEhs(string? presentationId) => presentationId?.Contains("EHS", StringComparison.Ordinal) == true;

    private static double? NonNegative(double? value) => value is >= 0 ? value : null;
}

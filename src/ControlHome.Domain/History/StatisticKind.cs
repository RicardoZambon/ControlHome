using ControlHome.Domain.Capabilities;

namespace ControlHome.Domain.History;

/// <summary>How an attribute's history is rolled up into statistics.</summary>
public enum StatisticKind
{
    /// <summary>Not rolled up (not recorded, not a number, or a state such as on/off): raw history only.</summary>
    None,

    /// <summary>A recorded number with <see cref="StateClass.Measurement"/>: time-weighted mean, min, max.</summary>
    Measurement,

    /// <summary>A recorded number with <see cref="StateClass.TotalIncreasing"/>: increases with reset detection.</summary>
    TotalIncreasing,

    /// <summary>A recorded number with <see cref="StateClass.Total"/>: signed differences.</summary>
    Total,
}

/// <summary>Which attributes get statistics and energy, from their definitions.</summary>
public static class HistoryClassification
{
    /// <summary>The statistics an attribute gets (<see cref="StatisticKind.None"/> for <c>null</c>).</summary>
    public static StatisticKind KindOf(AttributeDefinition? definition) =>
        definition is { Recorded: true, Schema: NumberSchema }
            ? definition.StateClass switch
            {
                StateClass.Measurement => StatisticKind.Measurement,
                StateClass.TotalIncreasing => StatisticKind.TotalIncreasing,
                StateClass.Total => StatisticKind.Total,
                _ => StatisticKind.None,
            }
            : StatisticKind.None;

    /// <summary>
    /// The energy source of an endpoint from its (non-retired) attributes: an energy counter (Wh, recorded, the catalogue's
    /// <c>energy.energy</c> first), else vendor windows (when the connector reports them), else power readings (W
    /// measurement, recorded, the catalogue's <c>power.power</c> first); <c>null</c> when it has none of them.
    /// </summary>
    public static (EnergySource Source, int? AttributeId)? PickEnergySource(IReadOnlyList<EnergyCandidate> attributes, bool hasVendorWindows)
    {
        ArgumentNullException.ThrowIfNull(attributes);

        var counter = attributes
            .Where(a => a.Definition.Unit == Units.WattHour && KindOf(a.Definition) is StatisticKind.TotalIncreasing or StatisticKind.Total)
            .OrderBy(a => a.CapabilityId == CapabilityIds.Energy ? 0 : 1)
            .ThenBy(a => KindOf(a.Definition) == StatisticKind.TotalIncreasing ? 0 : 1)
            .ThenBy(a => a.AttributeId)
            .FirstOrDefault();
        if (counter is not null)
        {
            return (EnergySource.Counter, counter.AttributeId);
        }

        if (hasVendorWindows)
        {
            return (EnergySource.VendorWindow, null);
        }

        var power = attributes
            .Where(a => a.Definition.Unit == Units.Watt && KindOf(a.Definition) == StatisticKind.Measurement)
            .OrderBy(a => a.CapabilityId == CapabilityIds.Power ? 0 : 1)
            .ThenBy(a => a.AttributeId)
            .FirstOrDefault();
        return power is null ? null : (EnergySource.IntegratedPower, power.AttributeId);
    }
}

/// <summary>An attribute of an endpoint considered for its energy source.</summary>
public sealed record EnergyCandidate(int AttributeId, string CapabilityId, AttributeDefinition Definition);

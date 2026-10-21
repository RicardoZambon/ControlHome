using System.Diagnostics.CodeAnalysis;

namespace ControlHome.Domain.History;

/// <summary>Where an endpoint's energy comes from (stored as its <see cref="EnergySources.Name"/>).</summary>
public enum EnergySource
{
    /// <summary>An energy counter (Wh, <c>total_increasing</c>/<c>total</c>): the hourly increase.</summary>
    Counter,

    /// <summary>Energy reported per interval by the device (reserved).</summary>
    Delta,

    /// <summary>Energy windows reported by a vendor cloud, spread across hours (Phase 2 connectors).</summary>
    VendorWindow,

    /// <summary>Power readings (W) integrated over time — an estimate.</summary>
    IntegratedPower,
}

/// <summary>The stable names of <see cref="EnergySource"/> (API and database).</summary>
public static class EnergySources
{
    public const int NameMaxLength = 16;

    private static readonly Dictionary<EnergySource, string> _names = new()
    {
        [EnergySource.Counter] = "counter",
        [EnergySource.Delta] = "delta",
        [EnergySource.VendorWindow] = "vendorWindow",
        [EnergySource.IntegratedPower] = "integratedPower",
    };

    private static readonly Dictionary<string, EnergySource> _byName = _names.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);

    public static string Name(EnergySource source) => _names[source];

    public static bool TryParse(string? name, [NotNullWhen(true)] out EnergySource? source)
    {
        if (name is not null && _byName.TryGetValue(name, out var found))
        {
            source = found;
            return true;
        }

        source = null;
        return false;
    }

    public static EnergySource Parse(string name) =>
        TryParse(name, out var source) ? source.Value : throw new ArgumentException($"Unknown energy source '{name}'.", nameof(name));
}

/// <summary>
/// The energy one endpoint used in one hour (table <c>EnergyHourly</c>, unique <c>(EndpointId, HourStartUtc)</c>): the
/// single source of every energy view. <see cref="Estimated"/> when integrated from power.
/// </summary>
public sealed class EnergyHourly
{
    public Guid EndpointId { get; set; }

    /// <summary>The hour's start (UTC).</summary>
    public DateTimeOffset HourStartUtc { get; set; }

    public double Wh { get; set; }

    public EnergySource Source { get; set; }

    public bool Estimated { get; set; }
}

/// <summary>
/// The one energy source of an endpoint (table <c>EnergySourceSelection</c>), so its energy is never counted twice. Picked
/// automatically by the rollup — a counter, else vendor windows, else integrated power — unless
/// <see cref="IsUserOverride"/> (set by the user; the rollup never changes it).
/// </summary>
public sealed class EnergySourceSelection
{
    public Guid EndpointId { get; set; }

    public EnergySource Source { get; set; }

    /// <summary>The attribute read for <see cref="EnergySource.Counter"/> (Wh) or <see cref="EnergySource.IntegratedPower"/> (W).</summary>
    public int? AttributeId { get; set; }

    public bool IsUserOverride { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

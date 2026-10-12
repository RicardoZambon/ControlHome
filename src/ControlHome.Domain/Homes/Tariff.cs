namespace ControlHome.Domain.Homes;

/// <summary>Electricity tariff used for energy cost estimates (stored as JSON on <see cref="Home"/>).</summary>
public sealed class Tariff
{
    /// <summary>The only kind supported so far: a flat price per kWh.</summary>
    public const string FlatKind = "flat";

    public string Kind { get; set; } = FlatKind;

    /// <summary>Price per kWh in the home's currency; <c>null</c> when unknown.</summary>
    public decimal? PricePerKwh { get; set; }
}

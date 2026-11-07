using ControlHome.Domain.Capabilities;

namespace ControlHome.Connectors.SmartThings.Mapping;

/// <summary>How a vendor value is turned into its canonical unit (and back, for command arguments).</summary>
internal enum UnitConversion
{
    None,

    /// <summary>°F ↔ °C.</summary>
    FahrenheitToCelsius,

    /// <summary>kWh ↔ Wh, kW ↔ W (× 1000).</summary>
    Kilo,
}

/// <summary>SmartThings units (<c>C</c>, <c>F</c>, <c>kWh</c>, <c>μg/m^3</c>, …) and their canonical counterparts.</summary>
internal static class StUnits
{
    /// <summary>The canonical unit for a SmartThings unit, and the conversion to apply to values.</summary>
    public static (string? Unit, UnitConversion Conversion) Canonical(string? unit) => unit?.Trim() switch
    {
        null or "" => (null, UnitConversion.None),
        "C" or "°C" or "celsius" => (Units.Celsius, UnitConversion.None),
        "F" or "°F" or "fahrenheit" => (Units.Celsius, UnitConversion.FahrenheitToCelsius),
        "kWh" => (Units.WattHour, UnitConversion.Kilo),
        "Wh" => (Units.WattHour, UnitConversion.None),
        "kW" => (Units.Watt, UnitConversion.Kilo),
        "W" => (Units.Watt, UnitConversion.None),
        "%" => (Units.Percent, UnitConversion.None),
        "K" => (Units.Kelvin, UnitConversion.None),
        "min" or "minutes" => (Units.Minute, UnitConversion.None),
        "ppm" => (Units.PartsPerMillion, UnitConversion.None),
        "ppb" => (Units.PartsPerBillion, UnitConversion.None),
        "μg/m^3" or "µg/m^3" or "μg/m³" or "µg/m³" or "ug/m3" => (Units.MicrogramsPerCubicMeter, UnitConversion.None),
        "lux" => ("lx", UnitConversion.None),
        { } other => (other.Length <= 16 ? other : other[..16], UnitConversion.None),
    };

    /// <summary>A vendor value in its canonical unit.</summary>
    public static double ToCanonical(double value, UnitConversion conversion) => conversion switch
    {
        UnitConversion.FahrenheitToCelsius => Math.Round((value - 32) * 5 / 9, 2),
        UnitConversion.Kilo => value * 1000,
        _ => value,
    };

    /// <summary>A canonical value in the vendor's unit (command arguments).</summary>
    public static double FromCanonical(double value, UnitConversion conversion) => conversion switch
    {
        UnitConversion.FahrenheitToCelsius => Math.Round((value * 9 / 5) + 32, 1),
        UnitConversion.Kilo => value / 1000,
        _ => value,
    };

    /// <summary>A temperature in °C from a value and its SmartThings unit (<c>C</c> when absent).</summary>
    public static double Celsius(double value, string? unit) => ToCanonical(value, Canonical(unit ?? "C").Conversion);

    /// <summary>Rounds to the nearest multiple of <paramref name="step"/> (counted from <paramref name="origin"/>).</summary>
    public static double Snap(double value, double step, double origin = 0) =>
        step <= 0 ? value : Math.Round((Math.Round((value - origin) / step) * step) + origin, 6);
}

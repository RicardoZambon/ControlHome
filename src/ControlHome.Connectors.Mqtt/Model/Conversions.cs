using System.Globalization;
using System.Text.Json;

namespace ControlHome.Connectors.Mqtt.Model;

/// <summary>Unit and color conversions to the canonical units (W, Wh, °C, %, K).</summary>
internal static class Conversions
{
    public const string Volt = "V";
    public const string Ampere = "A";
    public const string Lux = "lx";
    public const string HectoPascal = "hPa";

    /// <summary>A multiplier to the canonical unit for a vendor unit, or <c>null</c> when the unit is unknown for the quantity.</summary>
    public static Func<double, double>? ToCanonical(string quantity, string? unit)
    {
        var u = unit?.Trim();
        return quantity switch
        {
            "temperature" => u switch
            {
                null or "" or "°C" or "C" or "℃" => v => v,
                "°F" or "F" or "℉" => v => (v - 32) * 5 / 9,
                "K" => v => v - 273.15,
                _ => null,
            },
            "power" => u switch
            {
                null or "" or "W" => v => v,
                "kW" => v => v * 1000,
                "mW" => v => v / 1000,
                _ => null,
            },
            "energy" => u switch
            {
                "Wh" => v => v,
                "kWh" => v => v * 1000,
                "MWh" => v => v * 1_000_000,
                _ => null,
            },
            "voltage" => u switch
            {
                null or "" or "V" => v => v,
                "mV" => v => v / 1000,
                "kV" => v => v * 1000,
                _ => null,
            },
            "current" => u switch
            {
                null or "" or "A" => v => v,
                "mA" => v => v / 1000,
                _ => null,
            },
            "percent" => u is null or "" or "%" ? v => v : null,
            _ => null,
        };
    }

    /// <summary>Rounds to <paramref name="digits"/> decimals and clamps into [min, max].</summary>
    public static double Clamp(double value, double min, double max, int digits = 0) =>
        Math.Clamp(Math.Round(value, digits, MidpointRounding.AwayFromZero), min, max);

    /// <summary>Mired (reciprocal megakelvin) ↔ kelvin, rounded to whole kelvin.</summary>
    public static double MiredToKelvin(double mired) => mired <= 0 ? 0 : Math.Round(1_000_000 / mired);

    public static double KelvinToMired(double kelvin) => kelvin <= 0 ? 0 : Math.Round(1_000_000 / kelvin);

    /// <summary>
    /// CIE 1931 xy (brightness 1) → hue (0–360) and saturation (0–100), through linear sRGB (D65, Wide RGB gamut matrix
    /// as used by Philips Hue and Zigbee lights) and HSV.
    /// </summary>
    public static (double Hue, double Saturation) XyToHs(double x, double y)
    {
        if (y <= 0)
        {
            return (0, 0);
        }

        var z = 1 - x - y;
        var bigY = 1.0;
        var bigX = bigY / y * x;
        var bigZ = bigY / y * z;

        var r = (bigX * 1.656492) - (bigY * 0.354851) - (bigZ * 0.255038);
        var g = (-bigX * 0.707196) + (bigY * 1.655397) + (bigZ * 0.036152);
        var b = (bigX * 0.051713) - (bigY * 0.121364) + (bigZ * 1.011530);

        r = Math.Max(0, r);
        g = Math.Max(0, g);
        b = Math.Max(0, b);
        var max = Math.Max(r, Math.Max(g, b));
        if (max <= 0)
        {
            return (0, 0);
        }

        r /= max;
        g /= max;
        b /= max;
        var min = Math.Min(r, Math.Min(g, b));
        var delta = 1 - min;
        double hue;
        if (delta <= 1e-9)
        {
            hue = 0;
        }
        else if (r >= 1)
        {
            hue = 60 * (((g - b) / delta) % 6);
        }
        else if (g >= 1)
        {
            hue = 60 * (((b - r) / delta) + 2);
        }
        else
        {
            hue = 60 * (((r - g) / delta) + 4);
        }

        if (hue < 0)
        {
            hue += 360;
        }

        return (Math.Round(hue, 1), Math.Round(delta * 100));
    }

    /// <summary>Hue (0–360) and saturation (0–100) at full brightness → CIE 1931 xy (inverse of <see cref="XyToHs"/>).</summary>
    public static (double X, double Y) HsToXy(double hue, double saturation)
    {
        var s = Math.Clamp(saturation, 0, 100) / 100;
        var h = ((hue % 360) + 360) % 360 / 60;
        var c = s;
        var xComponent = c * (1 - Math.Abs((h % 2) - 1));
        var m = 1 - c;
        var (r, g, b) = (int)h switch
        {
            0 => (c, xComponent, 0d),
            1 => (xComponent, c, 0d),
            2 => (0d, c, xComponent),
            3 => (0d, xComponent, c),
            4 => (xComponent, 0d, c),
            _ => (c, 0d, xComponent),
        };
        r += m;
        g += m;
        b += m;

        var bigX = (r * 0.664511) + (g * 0.154324) + (b * 0.162028);
        var bigY = (r * 0.283881) + (g * 0.668433) + (b * 0.047685);
        var bigZ = (r * 0.000088) + (g * 0.072310) + (b * 0.986039);
        var sum = bigX + bigY + bigZ;
        return sum <= 0 ? (0, 0) : (Math.Round(bigX / sum, 4), Math.Round(bigY / sum, 4));
    }

    /// <summary>A JSON number, or a string holding an invariant number; <c>null</c> otherwise.</summary>
    public static double? Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetDouble(out var number) && double.IsFinite(number) => number,
        JsonValueKind.String => Number(value.GetString()),
        _ => null,
    };

    /// <summary>An invariant number in text (<c>"21.5"</c>); <c>null</c> otherwise.</summary>
    public static double? Number(string? text) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;

    /// <summary>A number as compact invariant text (<c>21.5</c>, <c>100</c>).</summary>
    public static string Format(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}

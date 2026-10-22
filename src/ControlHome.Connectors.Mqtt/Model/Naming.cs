using System.Globalization;
using System.Text;

namespace ControlHome.Connectors.Mqtt.Model;

/// <summary>Turns vendor names (<c>local_temperature_calibration</c>, <c>DS18B20-1</c>) into keys.</summary>
internal static class Naming
{
    /// <summary>
    /// camelCase from snake_case, kebab-case, spaces or PascalCase: <c>local_temperature</c> → <c>localTemperature</c>,
    /// <c>ApparentPower</c> → <c>apparentPower</c>, <c>PM2.5</c> → <c>pm25</c>. Never empty (falls back to <c>value</c>).
    /// </summary>
    public static string CamelCase(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var words = Words(name);
        if (words.Count == 0)
        {
            return "value";
        }

        var result = new StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            var allUpper = word.All(c => !char.IsLetter(c) || char.IsUpper(c));
            var normalized = allUpper ? word.ToLowerInvariant() : char.ToLowerInvariant(word[0]) + word[1..];
            result.Append(i == 0 ? normalized : char.ToUpperInvariant(normalized[0]) + normalized[1..]);
        }

        var key = result.ToString();
        return char.IsDigit(key[0]) ? "v" + key : key;
    }

    /// <summary>PascalCase (for command names such as <c>setLocalTemperatureCalibration</c>).</summary>
    public static string PascalCase(string name)
    {
        var camel = CamelCase(name);
        return char.ToUpperInvariant(camel[0]) + camel[1..];
    }

    /// <summary>A lower-case endpoint key: letters, digits, <c>_</c> and <c>-</c> only, at most 64 characters.</summary>
    public static string EndpointKey(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var builder = new StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        }

        var key = builder.ToString().Trim('_');
        if (key.Length == 0)
        {
            key = "endpoint";
        }

        return key.Length <= 64 ? key : key[..64];
    }

    /// <summary>A key that is not in <paramref name="taken"/>: <paramref name="key"/>, then <c>key2</c>, <c>key3</c>, …</summary>
    public static string Unique(string key, ISet<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        if (taken.Add(key))
        {
            return key;
        }

        for (var i = 2; ; i++)
        {
            var candidate = key + i.ToString(CultureInfo.InvariantCulture);
            if (taken.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static List<string> Words(string name)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsLetterOrDigit(c))
            {
                // Separators split words, except a dot between digits (PM2.5 → pm25).
                Flush();
                continue;
            }

            // An upper-case letter after a lower-case letter or digit starts a new word (camelCase / PascalCase).
            if (char.IsUpper(c) && current.Length > 0 && (char.IsLower(current[^1]) || char.IsDigit(current[^1])))
            {
                Flush();
            }

            current.Append(c);
        }

        Flush();
        return words;

        void Flush()
        {
            if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }
    }
}

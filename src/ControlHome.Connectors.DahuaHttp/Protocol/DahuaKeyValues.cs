using System.Globalization;
using System.Text.RegularExpressions;

namespace ControlHome.Connectors.DahuaHttp.Protocol;

/// <summary>
/// A plain-text CGI answer of the Dahua HTTP API: one <c>key=value</c> per line (<c>type=VIP 1230 B</c>,
/// <c>table.ChannelTitle[0].Name=Garagem</c>), or a bare <c>OK</c> / <c>Error</c>. Lines without <c>=</c> are kept only as
/// <see cref="Lines"/>; the first occurrence of a key wins.
/// </summary>
internal sealed partial class DahuaKeyValues
{
    private readonly Dictionary<string, string> _values;

    private DahuaKeyValues(IReadOnlyList<KeyValuePair<string, string>> entries, IReadOnlyList<string> lines)
    {
        Entries = entries;
        Lines = lines;
        _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in entries)
        {
            _values.TryAdd(key, value);
        }
    }

    /// <summary>Every <c>key=value</c> line, in order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Entries { get; }

    /// <summary>Every non-empty line, trimmed.</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>The answer is a bare <c>OK</c> (setters, <c>openDoor</c>).</summary>
    public bool IsOk => Lines.Count > 0 && string.Equals(Lines[0], "OK", StringComparison.OrdinalIgnoreCase);

    /// <summary>The value of <paramref name="key"/> (case-insensitive), or <c>null</c>.</summary>
    public string? this[string key] => _values.GetValueOrDefault(key);

    public static DahuaKeyValues Parse(string? text)
    {
        var entries = new List<KeyValuePair<string, string>>();
        var lines = new List<string>();
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            lines.Add(line);
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                entries.Add(new KeyValuePair<string, string>(line[..equals].Trim(), line[(equals + 1)..].Trim()));
            }
        }

        return new DahuaKeyValues(entries, lines);
    }

    /// <summary>
    /// The channel titles of <c>configManager.cgi?action=getConfig&amp;name=ChannelTitle</c>
    /// (<c>table.ChannelTitle[&lt;index&gt;].Name=&lt;title&gt;</c>), by 1-based channel number.
    /// </summary>
    public SortedDictionary<int, string> ChannelTitles()
    {
        var titles = new SortedDictionary<int, string>();
        foreach (var (key, value) in Entries)
        {
            var match = ChannelTitleKey().Match(key);
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && index < 1024)
            {
                titles.TryAdd(index + 1, value);
            }
        }

        return titles;
    }

    /// <summary>
    /// The enabled smart (IVS) rules of <c>configManager.cgi?action=getConfig&amp;name=VideoAnalyseRule</c>
    /// (<c>table.VideoAnalyseRule[&lt;channel&gt;][&lt;rule&gt;].Type</c> / <c>.Enable</c>): the rule types per 1-based channel.
    /// A rule without an <c>Enable</c> line counts as enabled.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlySet<string>> EnabledRuleTypes()
    {
        var rules = new Dictionary<(int Channel, int Rule), (string? Type, bool Enabled)>();
        foreach (var (key, value) in Entries)
        {
            var match = VideoAnalyseRuleKey().Match(key);
            if (!match.Success
                || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var channel)
                || !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var rule)
                || channel >= 1024)
            {
                continue;
            }

            var current = rules.GetValueOrDefault((channel, rule), (null, true));
            rules[(channel, rule)] = match.Groups[3].Value.Equals("Type", StringComparison.OrdinalIgnoreCase)
                ? (value, current.Enabled)
                : (current.Type, !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase));
        }

        var result = new Dictionary<int, IReadOnlySet<string>>();
        foreach (var group in rules.Where(r => r.Value is { Type.Length: > 0, Enabled: true }).GroupBy(r => r.Key.Channel + 1))
        {
            result[group.Key] = group.Select(r => r.Value.Type!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return result;
    }

    /// <summary>
    /// The channel indexes (0-based, as in events) of <c>eventManager.cgi?action=getEventIndexes</c>
    /// (<c>channels[0]=2</c>, …): the channels where the event is active right now.
    /// </summary>
    public IReadOnlySet<int> ChannelIndexes()
    {
        var channels = new HashSet<int>();
        foreach (var (key, value) in Entries)
        {
            if (key.StartsWith("channels[", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var channel)
                && channel < 1024)
            {
                channels.Add(channel);
            }
        }

        return channels;
    }

    [GeneratedRegex(@"^table\.ChannelTitle\[(\d{1,4})\]\.Name$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChannelTitleKey();

    [GeneratedRegex(@"^table\.VideoAnalyseRule\[(\d{1,4})\]\[(\d{1,4})\]\.(Type|Enable)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VideoAnalyseRuleKey();
}

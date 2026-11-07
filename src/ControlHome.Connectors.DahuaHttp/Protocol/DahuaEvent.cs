using System.Globalization;
using System.Text.Json;

namespace ControlHome.Connectors.DahuaHttp.Protocol;

/// <summary>
/// One event of the attach stream: <c>Code=VideoMotion;action=Start;index=0</c>, optionally with
/// <c>;data={…}</c> (a JSON object, possibly over several lines).
/// </summary>
/// <param name="Code">The event code (<c>VideoMotion</c>, <c>AlarmLocal</c>, <c>AccessControl</c>, …).</param>
/// <param name="Action"><c>Start</c>, <c>Stop</c> or <c>Pulse</c> (as sent; compare case-insensitively).</param>
/// <param name="Index">The 0-based channel (video channel, alarm input, door), when present.</param>
/// <param name="Data">The event's data object, when present and valid JSON.</param>
internal sealed record DahuaEvent(string Code, string? Action, int? Index, JsonElement? Data)
{
    public bool IsStart => string.Equals(Action, "Start", StringComparison.OrdinalIgnoreCase);

    public bool IsStop => string.Equals(Action, "Stop", StringComparison.OrdinalIgnoreCase);

    public bool IsPulse => string.Equals(Action, "Pulse", StringComparison.OrdinalIgnoreCase);

    /// <summary>A member of <see cref="Data"/> (case-insensitive), or <c>null</c>.</summary>
    public JsonElement? DataMember(string name)
    {
        if (Data is not { ValueKind: JsonValueKind.Object } data)
        {
            return null;
        }

        foreach (var property in data.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    /// <summary>A string or number member of <see cref="Data"/> as text, or <c>null</c>.</summary>
    public string? DataText(string name) => DataMember(name) switch
    {
        { ValueKind: JsonValueKind.String } value => value.GetString(),
        { ValueKind: JsonValueKind.Number } value => value.GetRawText(),
        _ => null,
    };

    /// <summary>An integer member of <see cref="Data"/> (a JSON number or a numeric string), or <c>null</c>.</summary>
    public long? DataInteger(string name) => DataMember(name) switch
    {
        { ValueKind: JsonValueKind.Number } value when value.TryGetInt64(out var number) => number,
        { ValueKind: JsonValueKind.String } value
            when long.TryParse(value.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) => number,
        _ => null,
    };
}

/// <summary>Turns the text of one multipart part into events (a part may carry several; a heartbeat carries none).</summary>
internal static class DahuaEventParser
{
    /// <summary>The longest <c>data</c> object parsed (larger ones are dropped; the event itself is kept).</summary>
    public const int MaxDataLength = 32 * 1024;

    private static readonly JsonDocumentOptions _json = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 32,
    };

    /// <summary>The part is the stream's keep-alive (<c>Heartbeat</c>).</summary>
    public static bool IsHeartbeat(string body) => string.Equals(body.Trim(), "Heartbeat", StringComparison.OrdinalIgnoreCase);

    /// <summary>The events in a part's text; unparsable text yields none (never throws).</summary>
    public static IReadOnlyList<DahuaEvent> Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || IsHeartbeat(body))
        {
            return [];
        }

        var text = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var events = new List<DahuaEvent>();
        foreach (var segment in Segments(text))
        {
            if (ParseSegment(segment) is { } parsed)
            {
                events.Add(parsed);
            }
        }

        return events;
    }

    /// <summary>The text from each <c>Code=</c> that starts a line up to the next one.</summary>
    private static IEnumerable<string> Segments(string text)
    {
        var starts = new List<int>();
        for (var i = text.IndexOf("Code=", StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf("Code=", i + 5, StringComparison.OrdinalIgnoreCase))
        {
            if (i == 0 || text[i - 1] == '\n')
            {
                starts.Add(i);
            }
        }

        for (var s = 0; s < starts.Count; s++)
        {
            var end = s + 1 < starts.Count ? starts[s + 1] : text.Length;
            yield return text[starts[s]..end];
        }
    }

    private static DahuaEvent? ParseSegment(string segment)
    {
        string? code = null;
        string? action = null;
        int? index = null;
        JsonElement? data = null;
        var position = 0;
        while (position < segment.Length)
        {
            var equals = segment.IndexOf('=', position);
            if (equals < 0)
            {
                break;
            }

            var key = segment[position..equals].Trim().Trim(';').Trim();
            if (key.Equals("data", StringComparison.OrdinalIgnoreCase))
            {
                data = ParseData(segment[(equals + 1)..]);
                break;
            }

            var end = segment.IndexOfAny([';', '\n'], equals + 1);
            if (end < 0)
            {
                end = segment.Length;
            }

            var value = segment[(equals + 1)..end].Trim();
            if (key.Equals("Code", StringComparison.OrdinalIgnoreCase))
            {
                code = value;
            }
            else if (key.Equals("action", StringComparison.OrdinalIgnoreCase))
            {
                action = value;
            }
            else if (key.Equals("index", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                index = parsed;
            }

            position = end + 1;
        }

        return string.IsNullOrEmpty(code) ? null : new DahuaEvent(code, string.IsNullOrEmpty(action) ? null : action, index, data);
    }

    private static JsonElement? ParseData(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxDataLength || trimmed[0] is not ('{' or '['))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed, _json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

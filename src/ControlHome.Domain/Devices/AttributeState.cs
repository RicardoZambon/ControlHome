using System.Text.Json;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Domain.Devices;

/// <summary>
/// The persisted current value of one attribute (written behind by the state pipeline; the in-memory cache is the live
/// source). The value is spread over typed columns by <see cref="Kind"/>: booleans and numbers in <see cref="Num"/>
/// (booleans as 1/0), strings of up to <see cref="MaxStringLength"/> characters in <see cref="Str"/>, longer strings and
/// JSON objects/arrays in <see cref="Json"/>. Use <see cref="GetValue"/> / <see cref="SetValue"/> (or
/// <see cref="Encode"/> / <see cref="Decode"/>) rather than the columns.
/// </summary>
public sealed class AttributeState
{
    /// <summary>The longest string kept in <see cref="Str"/> (<c>nvarchar(255)</c>).</summary>
    public const int MaxStringLength = 255;

    /// <summary>The <see cref="DeviceAttribute.Id"/> (primary key).</summary>
    public int AttributeId { get; set; }

    public double? Num { get; set; }

    public string? Str { get; set; }

    /// <summary>JSON text (<c>json</c> column).</summary>
    public string? Json { get; set; }

    public StateValueKind Kind { get; set; }

    /// <summary>When the device observed the value (or when it was reported, if the connector gave no time).</summary>
    public DateTimeOffset ObservedAtUtc { get; set; }

    /// <summary>When the value last became different (drives <c>for</c> durations of automations after a restart).</summary>
    public DateTimeOffset LastChangedAtUtc { get; set; }

    /// <summary>When this row was last written.</summary>
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public StateValue GetValue() => Decode(Kind, Num, Str, Json);

    /// <summary>
    /// Stores <paramref name="value"/> observed at <paramref name="observedAtUtc"/>; <see cref="LastChangedAtUtc"/> moves
    /// only when the value differs from the stored one (or nothing was stored yet).
    /// </summary>
    /// <returns>Whether the value changed.</returns>
    public bool SetValue(StateValue value, DateTimeOffset observedAtUtc, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(value);

        var isNew = LastChangedAtUtc == default;
        var changed = isNew || !GetValue().Equals(value);
        (Kind, Num, Str, Json) = Encode(value);
        ObservedAtUtc = observedAtUtc;
        UpdatedAtUtc = nowUtc;
        if (changed)
        {
            LastChangedAtUtc = observedAtUtc;
        }

        return changed;
    }

    /// <summary>The column values for <paramref name="value"/>.</summary>
    public static (StateValueKind Kind, double? Num, string? Str, string? Json) Encode(StateValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Kind switch
        {
            StateValueKind.Bool => (StateValueKind.Bool, value.B!.Value ? 1 : 0, null, null),
            StateValueKind.Number => (StateValueKind.Number, value.N, null, null),
            StateValueKind.String when value.S!.Length <= MaxStringLength => (StateValueKind.String, null, value.S, null),
            StateValueKind.String => (StateValueKind.String, null, null, JsonSerializer.Serialize(value.S)),
            StateValueKind.Json => (StateValueKind.Json, null, null, value.J!.Value.GetRawText()),
            _ => (StateValueKind.Null, null, null, null),
        };
    }

    /// <summary>The value stored in the columns (the inverse of <see cref="Encode"/>).</summary>
    /// <exception cref="InvalidOperationException">The columns do not hold a value of <paramref name="kind"/>.</exception>
    public static StateValue Decode(StateValueKind kind, double? num, string? str, string? json) => kind switch
    {
        StateValueKind.Null => StateValue.Null,
        StateValueKind.Bool when num is { } flag => StateValue.Bool(flag != 0),
        StateValueKind.Number when num is { } number => StateValue.Number(number),
        StateValueKind.String when str is not null => StateValue.String(str),
        StateValueKind.String when json is not null => StateValue.String(JsonSerializer.Deserialize<string>(json)
            ?? throw new InvalidOperationException("A long string state holds JSON null.")),
        StateValueKind.Json when json is not null => StateValue.Json(ParseJson(json)),
        _ => throw new InvalidOperationException($"The state columns hold no '{kind}' value."),
    };

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

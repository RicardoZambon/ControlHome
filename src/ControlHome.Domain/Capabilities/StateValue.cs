using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlHome.Domain.Capabilities;

/// <summary>Which member of a <see cref="StateValue"/> holds the value (stored as <c>tinyint</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StateValueKind>))]
public enum StateValueKind : byte
{
    /// <summary>No value (unknown or not reported yet).</summary>
    [JsonStringEnumMemberName("null")]
    Null = 0,

    [JsonStringEnumMemberName("bool")]
    Bool = 1,

    [JsonStringEnumMemberName("number")]
    Number = 2,

    [JsonStringEnumMemberName("string")]
    String = 3,

    /// <summary>A JSON object or array.</summary>
    [JsonStringEnumMemberName("json")]
    Json = 4,
}

/// <summary>
/// An attribute value as it travels in DTOs and hub messages: <c>{ kind: 'bool'|'number'|'string'|'json'|'null', b?, n?,
/// s?, j? }</c> — exactly the member named by <see cref="Kind"/> is set. Create values with the factories
/// (<see cref="Bool"/>, <see cref="Number"/>, <see cref="String"/>, <see cref="Json"/>, <see cref="Null"/>,
/// <see cref="FromJson"/>); equality compares JSON values structurally.
/// </summary>
public sealed record StateValue
{
    [JsonConstructor]
    private StateValue(StateValueKind kind, bool? b, double? n, string? s, JsonElement? j)
    {
        var valid = kind switch
        {
            StateValueKind.Null => b is null && n is null && s is null && j is null,
            StateValueKind.Bool => b is not null && n is null && s is null && j is null,
            StateValueKind.Number => n is { } number && double.IsFinite(number) && b is null && s is null && j is null,
            StateValueKind.String => s is not null && b is null && n is null && j is null,
            StateValueKind.Json => j is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } && b is null && n is null && s is null,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException($"A '{kind}' state value must set exactly its own member.", nameof(kind));
        }

        Kind = kind;
        B = b;
        N = n;
        S = s;
        J = j?.Clone();
    }

    /// <summary>The unknown / no-value state.</summary>
    public static StateValue Null { get; } = new(StateValueKind.Null, null, null, null, null);

    public static StateValue True { get; } = new(StateValueKind.Bool, true, null, null, null);

    public static StateValue False { get; } = new(StateValueKind.Bool, false, null, null, null);

    public StateValueKind Kind { get; }

    /// <summary>Set when <see cref="Kind"/> is <see cref="StateValueKind.Bool"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? B { get; }

    /// <summary>Set (finite) when <see cref="Kind"/> is <see cref="StateValueKind.Number"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? N { get; }

    /// <summary>Set when <see cref="Kind"/> is <see cref="StateValueKind.String"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? S { get; }

    /// <summary>A JSON object or array, set when <see cref="Kind"/> is <see cref="StateValueKind.Json"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? J { get; }

    public static StateValue Bool(bool value) => value ? True : False;

    /// <exception cref="ArgumentException">The number is NaN or infinite.</exception>
    public static StateValue Number(double value) => new(StateValueKind.Number, null, value, null, null);

    public static StateValue String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new StateValue(StateValueKind.String, null, null, value, null);
    }

    /// <summary>A JSON object or array (other JSON values map to their own kinds: use <see cref="FromJson"/>).</summary>
    public static StateValue Json(JsonElement value) => new(StateValueKind.Json, null, null, null, value);

    /// <summary>
    /// Maps any JSON value: <c>true</c>/<c>false</c> → bool, number → number, string → string, <c>null</c> (or an undefined
    /// element) → <see cref="Null"/>, object/array → json.
    /// </summary>
    public static StateValue FromJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => True,
        JsonValueKind.False => False,
        JsonValueKind.Number => Number(value.GetDouble()),
        JsonValueKind.String => String(value.GetString()!),
        JsonValueKind.Object or JsonValueKind.Array => Json(value),
        _ => Null,
    };

    /// <summary>The value as plain JSON (<c>true</c>, <c>21.5</c>, <c>"cool"</c>, an object/array, or <c>null</c>).</summary>
    public JsonElement ToJson() => Kind switch
    {
        StateValueKind.Bool => JsonSerializer.SerializeToElement(B!.Value),
        StateValueKind.Number => JsonSerializer.SerializeToElement(N!.Value),
        StateValueKind.String => JsonSerializer.SerializeToElement(S),
        StateValueKind.Json => J!.Value,
        _ => JsonSerializer.SerializeToElement<object?>(null),
    };

    public bool Equals(StateValue? other) =>
        other is not null
        && Kind == other.Kind
        && B == other.B
        && N.Equals(other.N)
        && string.Equals(S, other.S, StringComparison.Ordinal)
        && (J is null ? other.J is null : other.J is not null && JsonElement.DeepEquals(J.Value, other.J.Value));

    // JSON values hash by kind only (structural equality ignores formatting, so their text cannot be hashed).
    public override int GetHashCode() => HashCode.Combine(Kind, B, N, S is null ? 0 : StringComparer.Ordinal.GetHashCode(S));

    public override string ToString() => Kind switch
    {
        StateValueKind.Bool => B!.Value ? "true" : "false",
        StateValueKind.Number => N!.Value.ToString("R", CultureInfo.InvariantCulture),
        StateValueKind.String => S!,
        StateValueKind.Json => J!.Value.GetRawText(),
        _ => "null",
    };
}

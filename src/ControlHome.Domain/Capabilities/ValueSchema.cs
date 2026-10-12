using System.Text.Json.Serialization;

namespace ControlHome.Domain.Capabilities;

/// <summary>
/// The shape of one value: an attribute's state, a command argument or a connector configuration field. A closed set of
/// records serialized polymorphically with the discriminator <c>type</c> (<c>bool</c>, <c>number</c>, <c>enum</c>,
/// <c>string</c>, <c>flags</c>, <c>json</c>, <c>momentary</c>); the web app renders every schema-driven control from it.
/// </summary>
/// <remarks>
/// Values travel as <see cref="StateValue"/> (state) or plain JSON (command arguments, configuration);
/// <see cref="CommandValidator"/> and <see cref="ValueSchemaValidator"/> check them against a schema.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BoolSchema), "bool")]
[JsonDerivedType(typeof(NumberSchema), "number")]
[JsonDerivedType(typeof(EnumSchema), "enum")]
[JsonDerivedType(typeof(StringSchema), "string")]
[JsonDerivedType(typeof(FlagsSchema), "flags")]
[JsonDerivedType(typeof(JsonValueSchema), "json")]
[JsonDerivedType(typeof(MomentarySchema), "momentary")]
public abstract record ValueSchema
{
    // Closed hierarchy: only the records below exist (the JSON discriminators and the web app know exactly these).
    private protected ValueSchema()
    {
    }
}

/// <summary><c>true</c> or <c>false</c>.</summary>
public sealed record BoolSchema : ValueSchema
{
    public static BoolSchema Instance { get; } = new();
}

/// <summary>A finite number.</summary>
/// <param name="Min">Inclusive lower bound, if any.</param>
/// <param name="Max">Inclusive upper bound, if any.</param>
/// <param name="Step">Granularity: valid values are <c>(Min ?? 0) + k × Step</c>.</param>
/// <param name="Unit">Canonical unit (see <see cref="Units"/>), e.g. <c>%</c>, <c>W</c>, <c>°C</c>.</param>
/// <param name="Integer">Only whole numbers are valid.</param>
public sealed record NumberSchema(
    double? Min = null,
    double? Max = null,
    double? Step = null,
    string? Unit = null,
    bool Integer = false) : ValueSchema;

/// <summary>One of a fixed list of string values (an endpoint's constraints may allow only a subset).</summary>
/// <param name="Values">The allowed values, in display order; never empty.</param>
public sealed record EnumSchema(IReadOnlyList<string> Values) : ValueSchema
{
    public bool Equals(EnumSchema? other) => other is not null && Values.SequenceEqual(other.Values, StringComparer.Ordinal);

    public override int GetHashCode() => SequenceHash(Values);

    internal static int SequenceHash(IEnumerable<string> values)
    {
        var hash = new HashCode();
        foreach (var value in values)
        {
            hash.Add(value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}

/// <summary>Free text.</summary>
/// <param name="MaxLength">Maximum length in UTF-16 code units, if any.</param>
public sealed record StringSchema(int? MaxLength = null) : ValueSchema;

/// <summary>A set of named flags (a bitmap), as a JSON array of distinct flag names.</summary>
/// <param name="Flags">The known flags; never empty.</param>
public sealed record FlagsSchema(IReadOnlyList<string> Flags) : ValueSchema
{
    public bool Equals(FlagsSchema? other) => other is not null && Flags.SequenceEqual(other.Flags, StringComparer.Ordinal);

    public override int GetHashCode() => EnumSchema.SequenceHash(Flags);
}

/// <summary>Any JSON value (read-only data such as a vendor's raw structure).</summary>
public sealed record JsonValueSchema : ValueSchema
{
    public static JsonValueSchema Instance { get; } = new();
}

/// <summary>
/// An event without lasting state (a button press, a doorbell ring). A momentary attribute is updated only through
/// connector events (<c>IConnectorContext.ReportEvent</c>): its value is the last event name and its observed time is when
/// that event happened.
/// </summary>
public sealed record MomentarySchema : ValueSchema
{
    public static MomentarySchema Instance { get; } = new();

    /// <summary>The event names this attribute emits (e.g. <c>single</c>, <c>double</c>, <c>long</c>); empty means any.</summary>
    public IReadOnlyList<string> Events { get; init; } = [];

    public bool Equals(MomentarySchema? other) => other is not null && Events.SequenceEqual(other.Events, StringComparer.Ordinal);

    public override int GetHashCode() => EnumSchema.SequenceHash(Events);
}

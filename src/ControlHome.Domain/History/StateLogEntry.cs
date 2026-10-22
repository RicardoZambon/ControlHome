using ControlHome.Domain.Capabilities;

namespace ControlHome.Domain.History;

/// <summary>
/// One change of a <c>recorded</c> attribute (table <c>StateLog</c>, append-only, no key: clustered on
/// <c>(TsUtc, AttributeId)</c>). The value holds until the attribute's next row. Numbers and booleans (1/0) go to
/// <see cref="Num"/>, strings (and JSON text) to <see cref="Str"/> (cut to <see cref="MaxStringLength"/>); both
/// <c>null</c> = the value became unknown.
/// </summary>
public sealed class StateLogEntry
{
    /// <summary>The longest string kept in <see cref="Str"/> (<c>nvarchar(255)</c>); longer ones are cut.</summary>
    public const int MaxStringLength = 255;

    public int AttributeId { get; set; }

    /// <summary>When the device observed the value (stored as <c>datetime2(3)</c>, UTC).</summary>
    public DateTimeOffset TsUtc { get; set; }

    public double? Num { get; set; }

    public string? Str { get; set; }

    /// <summary>The columns for <paramref name="value"/>.</summary>
    public static (double? Num, string? Str) Encode(StateValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Kind switch
        {
            StateValueKind.Bool => (value.B!.Value ? 1 : 0, null),
            StateValueKind.Number => (value.N, null),
            StateValueKind.String => (null, Cut(value.S!)),
            StateValueKind.Json => (null, Cut(value.J!.Value.GetRawText())),
            _ => (null, null),
        };
    }

    private static string Cut(string text) => text.Length <= MaxStringLength ? text : text[..MaxStringLength];
}

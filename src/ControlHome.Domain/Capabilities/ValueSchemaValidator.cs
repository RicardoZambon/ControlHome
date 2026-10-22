using System.Text.Json;

namespace ControlHome.Domain.Capabilities;

/// <summary>Why a value does not fit its schema (the Application maps each kind to an error code).</summary>
public enum ValueErrorKind
{
    /// <summary>A required argument or field is missing or <c>null</c>.</summary>
    Required,

    /// <summary>The member is not defined by the command or form.</summary>
    Unknown,

    /// <summary>Wrong JSON type (e.g. a string for a number, a fraction for an integer, a non-object argument list).</summary>
    InvalidType,

    /// <summary>A number outside the schema's or the endpoint's range.</summary>
    OutOfRange,

    /// <summary>A number that is not on the step grid.</summary>
    NotOnStep,

    /// <summary>A value not in the allowed list (enum value, constrained subset, unknown flag or event).</summary>
    NotAllowed,

    /// <summary>A string longer than the schema's maximum length.</summary>
    TooLong,
}

/// <summary>Checks single values against a <see cref="ValueSchema"/> and optional per-endpoint constraints.</summary>
public static class ValueSchemaValidator
{
    /// <summary>Tolerance for step and integer checks (floating-point arithmetic, e.g. 0.1 + 0.2).</summary>
    private const double Epsilon = 1e-9;

    /// <summary>
    /// Whether a reported state value fits the schema: <see cref="StateValue.Null"/> (unknown) always does; momentary
    /// attributes take an event name. Use it to reject malformed connector reports.
    /// </summary>
    public static bool Accepts(ValueSchema schema, StateValue value, AttributeConstraint? constraint = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(value);
        return value.Kind == StateValueKind.Null || Check(schema, value.ToJson(), constraint) is null;
    }

    /// <summary>
    /// Checks a non-null JSON value. <paramref name="constraint"/> narrows the schema (its <see cref="AttributeConstraint.Cases"/>
    /// are not applied here: resolve the case first, see <see cref="CommandValidator"/>).
    /// </summary>
    /// <returns>The first problem found, or <c>null</c> when the value is valid.</returns>
    public static ValueErrorKind? Check(ValueSchema schema, JsonElement value, AttributeConstraint? constraint = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return schema switch
        {
            BoolSchema => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : ValueErrorKind.InvalidType,
            NumberSchema number => CheckNumber(number, value, constraint),
            EnumSchema enumeration => CheckChoice(enumeration.Values, value, constraint),
            StringSchema text => CheckString(text, value, constraint),
            FlagsSchema flags => CheckFlags(flags, value),
            MomentarySchema momentary => momentary.Events.Count == 0
                ? value.ValueKind == JsonValueKind.String ? null : ValueErrorKind.InvalidType
                : CheckChoice(momentary.Events, value, null),
            JsonValueSchema => null,
            _ => ValueErrorKind.InvalidType,
        };
    }

    private static ValueErrorKind? CheckNumber(NumberSchema schema, JsonElement value, AttributeConstraint? constraint)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
        {
            return ValueErrorKind.InvalidType;
        }

        if (schema.Integer && Math.Abs(number - Math.Round(number)) > Epsilon)
        {
            return ValueErrorKind.InvalidType;
        }

        var min = Larger(schema.Min, constraint?.Min);
        var max = Smaller(schema.Max, constraint?.Max);
        if ((min is { } lower && number < lower - Epsilon) || (max is { } upper && number > upper + Epsilon))
        {
            return ValueErrorKind.OutOfRange;
        }

        // The endpoint's step (from its own minimum) wins over the schema's.
        var (step, origin) = constraint?.Step is { } constrainedStep
            ? (constrainedStep, constraint.Min ?? schema.Min ?? 0)
            : (schema.Step ?? 0, schema.Min ?? 0);
        if (step > 0)
        {
            var steps = (number - origin) / step;
            if (Math.Abs(steps - Math.Round(steps)) > 1e-6)
            {
                return ValueErrorKind.NotOnStep;
            }
        }

        return null;
    }

    private static ValueErrorKind? CheckChoice(IReadOnlyList<string> values, JsonElement value, AttributeConstraint? constraint)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return ValueErrorKind.InvalidType;
        }

        var text = value.GetString()!;
        return values.Contains(text, StringComparer.Ordinal) && Allowed(constraint, text) ? null : ValueErrorKind.NotAllowed;
    }

    private static ValueErrorKind? CheckString(StringSchema schema, JsonElement value, AttributeConstraint? constraint)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return ValueErrorKind.InvalidType;
        }

        var text = value.GetString()!;
        if (schema.MaxLength is { } maxLength && text.Length > maxLength)
        {
            return ValueErrorKind.TooLong;
        }

        return Allowed(constraint, text) ? null : ValueErrorKind.NotAllowed;
    }

    private static ValueErrorKind? CheckFlags(FlagsSchema schema, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return ValueErrorKind.InvalidType;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return ValueErrorKind.InvalidType;
            }

            var flag = item.GetString()!;
            if (!schema.Flags.Contains(flag, StringComparer.Ordinal) || !seen.Add(flag))
            {
                return ValueErrorKind.NotAllowed;
            }
        }

        return null;
    }

    private static bool Allowed(AttributeConstraint? constraint, string value) =>
        constraint?.Values is not { } allowed || allowed.Contains(value, StringComparer.Ordinal);

    private static double? Larger(double? a, double? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    private static double? Smaller(double? a, double? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
}

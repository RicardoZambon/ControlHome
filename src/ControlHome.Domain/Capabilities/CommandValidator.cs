using System.Text.Json;

namespace ControlHome.Domain.Capabilities;

/// <summary>One invalid member of an arguments (or configuration) object.</summary>
/// <param name="Argument">The member's key; <c>null</c> when the whole object is invalid (e.g. not a JSON object).</param>
public sealed record ArgumentError(string? Argument, ValueErrorKind Kind);

/// <summary>The outcome of <see cref="CommandValidator.Validate"/>: valid, or the errors in argument order.</summary>
public sealed record ArgumentValidationResult(IReadOnlyList<ArgumentError> Errors)
{
    public static ArgumentValidationResult Valid { get; } = new([]);

    public bool IsValid => Errors.Count == 0;

    public bool Equals(ArgumentValidationResult? other) => other is not null && Errors.SequenceEqual(other.Errors);

    public override int GetHashCode() => Errors.Count;
}

/// <summary>
/// Validates a command's JSON arguments against its <see cref="CommandDefinition"/> and the endpoint's
/// <see cref="CapabilityConstraints"/>: every required argument present, no unknown members, every value of the right type,
/// in range, on the step grid and in the (possibly narrowed) set of allowed values. The command service rejects invalid
/// commands before they reach a connector; connectors may re-check.
/// </summary>
public static class CommandValidator
{
    /// <summary>Validates <paramref name="arguments"/> (a JSON object; <c>null</c>/undefined mean no arguments).</summary>
    /// <param name="constraints">The endpoint capability's constraints JSON, if any.</param>
    /// <param name="currentState">
    /// The capability's current attribute values by key, used to pick mode-dependent ranges
    /// (<see cref="AttributeConstraint.DependsOn"/>) when the arguments do not set that attribute themselves.
    /// </param>
    /// <exception cref="JsonException">The constraints JSON is malformed.</exception>
    public static ArgumentValidationResult Validate(
        CommandDefinition command,
        JsonElement arguments,
        JsonElement? constraints,
        IReadOnlyDictionary<string, StateValue>? currentState = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ValidateArguments(command.Arguments, arguments, CapabilityConstraints.Parse(constraints), currentState);
    }

    /// <summary>
    /// Validates any JSON object against argument definitions (also used for connector configuration forms, where the
    /// fields play the role of arguments).
    /// </summary>
    public static ArgumentValidationResult ValidateArguments(
        IReadOnlyList<ArgumentDefinition> definitions,
        JsonElement values,
        CapabilityConstraints? constraints = null,
        IReadOnlyDictionary<string, StateValue>? currentState = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        constraints ??= CapabilityConstraints.None;

        if (values.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            values = EmptyObject();
        }
        else if (values.ValueKind != JsonValueKind.Object)
        {
            return new ArgumentValidationResult([new ArgumentError(null, ValueErrorKind.InvalidType)]);
        }

        var errors = new List<ArgumentError>();
        foreach (var member in values.EnumerateObject())
        {
            if (!definitions.Any(d => string.Equals(d.Key, member.Name, StringComparison.Ordinal)))
            {
                errors.Add(new ArgumentError(member.Name, ValueErrorKind.Unknown));
            }
        }

        foreach (var definition in definitions)
        {
            if (!values.TryGetProperty(definition.Key, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                if (definition.Required)
                {
                    errors.Add(new ArgumentError(definition.Key, ValueErrorKind.Required));
                }

                continue;
            }

            var attribute = definition.Attribute ?? definition.Key;
            foreach (var constraint in Applicable(constraints.For(attribute), definitions, values, currentState))
            {
                if (ValueSchemaValidator.Check(definition.Schema, value, constraint) is { } kind)
                {
                    errors.Add(new ArgumentError(definition.Key, kind));
                    break;
                }
            }
        }

        return errors.Count == 0 ? ArgumentValidationResult.Valid : new ArgumentValidationResult(errors);
    }

    /// <summary>
    /// The constraints a value must satisfy: none, the base constraint, plus the case selected by its
    /// <see cref="AttributeConstraint.DependsOn"/> attribute (from the same arguments first, then the current state).
    /// </summary>
    private static IEnumerable<AttributeConstraint?> Applicable(
        AttributeConstraint? constraint,
        IReadOnlyList<ArgumentDefinition> definitions,
        JsonElement values,
        IReadOnlyDictionary<string, StateValue>? currentState)
    {
        yield return constraint;
        if (constraint is not { DependsOn: { } dependsOn, Cases: { } cases })
        {
            yield break;
        }

        var selector = definitions.FirstOrDefault(d => string.Equals(d.Attribute ?? d.Key, dependsOn, StringComparison.Ordinal)) is { } source
            && values.TryGetProperty(source.Key, out var sent) && sent.ValueKind == JsonValueKind.String
                ? sent.GetString()
                : currentState?.GetValueOrDefault(dependsOn) is { Kind: StateValueKind.String, S: { } current } ? current : null;
        if (selector is not null && cases.TryGetValue(selector, out var selected))
        {
            yield return selected;
        }
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}

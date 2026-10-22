using System.Text.Json;

namespace ControlHome.Domain.Capabilities;

/// <summary>
/// Per-endpoint narrowing of a capability (<c>EndpointCapability.ConstraintsJson</c>, supplied by the connector), e.g. an
/// AC whose target temperature goes from 16 to 30 °C in 0.5 steps (16–26 °C while heating) and that has no <c>dry</c>
/// mode:
/// <code>
/// { "attributes": {
///     "mode": { "values": ["off", "cool", "heat", "auto", "fan"] },
///     "targetTemperature": { "min": 16, "max": 30, "step": 0.5,
///                            "dependsOn": "mode", "cases": { "heat": { "min": 16, "max": 26 } } } } }
/// </code>
/// Constraints only ever narrow the catalogue schema; they apply to the attribute's value and to every command argument
/// bound to it (<see cref="ArgumentDefinition.Attribute"/>).
/// </summary>
/// <param name="Attributes">Constraints by attribute key.</param>
public sealed record CapabilityConstraints(IReadOnlyDictionary<string, AttributeConstraint>? Attributes)
{
    public static CapabilityConstraints None { get; } = new((IReadOnlyDictionary<string, AttributeConstraint>?)null);

    /// <summary>The constraint for attribute <paramref name="key"/>, or <c>null</c>.</summary>
    public AttributeConstraint? For(string key) => Attributes?.GetValueOrDefault(key);

    /// <summary>Parses constraints JSON; <c>null</c>, an undefined element or JSON <c>null</c> mean none.</summary>
    /// <exception cref="JsonException">The JSON is not a constraints object.</exception>
    public static CapabilityConstraints Parse(JsonElement? json) =>
        json is not { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } element
            ? None
            : element.ValueKind == JsonValueKind.Object
                ? CapabilityJson.Deserialize<CapabilityConstraints>(element) ?? None
                : throw new JsonException("Capability constraints must be a JSON object.");

    /// <summary>Like <see cref="Parse"/>, but returns <c>false</c> instead of throwing on malformed JSON.</summary>
    public static bool TryParse(JsonElement? json, out CapabilityConstraints constraints)
    {
        try
        {
            constraints = Parse(json);
            return true;
        }
        catch (JsonException)
        {
            constraints = None;
            return false;
        }
    }
}

/// <summary>Narrowing of one attribute (and the arguments bound to it).</summary>
/// <param name="Min">Inclusive lower bound (numbers).</param>
/// <param name="Max">Inclusive upper bound (numbers).</param>
/// <param name="Step">Granularity from <paramref name="Min"/> (numbers).</param>
/// <param name="Values">The allowed values (enums: a subset of the schema's values; strings: the only accepted values).</param>
/// <param name="DependsOn">
/// Another attribute of the same capability (e.g. <c>mode</c>) whose current value selects one of <paramref name="Cases"/>.
/// </param>
/// <param name="Cases">Extra narrowing by the value of <paramref name="DependsOn"/>; applied on top of this constraint.</param>
public sealed record AttributeConstraint(
    double? Min = null,
    double? Max = null,
    double? Step = null,
    IReadOnlyList<string>? Values = null,
    string? DependsOn = null,
    IReadOnlyDictionary<string, AttributeConstraint>? Cases = null);

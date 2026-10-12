using System.Text.Json;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Connectors.Abstractions;

/// <summary>
/// Validates a submitted form (integration config, device config, a flow's <see cref="FormStep"/>) against its
/// <see cref="FieldSchema"/>s with the same rules as command arguments (<see cref="CommandValidator"/>): required fields
/// present, no unknown members, values matching their schema.
/// </summary>
public static class FieldSchemaValidator
{
    public static ArgumentValidationResult Validate(IReadOnlyList<FieldSchema> fields, JsonElement values)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return CommandValidator.ValidateArguments(
            fields.Select(f => new ArgumentDefinition(f.Key, f.Schema, f.Required)).ToList(),
            values);
    }
}

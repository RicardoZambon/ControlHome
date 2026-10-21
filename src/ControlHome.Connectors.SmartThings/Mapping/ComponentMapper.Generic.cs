using System.Text.Json;
using ControlHome.Connectors.SmartThings.Model;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Connectors.SmartThings.Mapping;

/// <summary>
/// The endpoint's <c>generic</c> capability: every capability without a catalogue mapping, described by its SmartThings
/// capability definition (value schemas, setters, commands). Lists of options (<c>supported…</c>, <c>available…</c>) and
/// ranges with a sibling attribute (<c>coolingSetpointRange</c>) are not attributes of their own; a range narrows its
/// sibling. Sensitive attributes and commands are skipped; capabilities that may open or unlock something get no
/// generic commands (<see cref="StCapabilities.IsSensitiveToControl"/>). Without a definition (custom namespaces) the
/// attributes are inferred from their values, read-only.
/// </summary>
internal sealed partial class ComponentMapper
{
    private void MapGeneric()
    {
        foreach (var capability in _component.Capabilities)
        {
            if (!Has(capability.Id) || _consumed.Contains(capability.Id))
            {
                continue;
            }

            var lookup = _context.Definitions(capability);
            switch (lookup.State)
            {
                case DefinitionState.Known:
                    FromDefinition(capability.Id, lookup.Document!);
                    break;
                case DefinitionState.Unavailable:
                    Inferred(capability.Id);
                    break;
                default:
                    _missing.Add(capability);
                    break;
            }
        }
    }

    private void FromDefinition(string capabilityId, CapabilityDocument definition)
    {
        var readOnly = StCapabilities.IsSensitiveToControl(capabilityId);
        var names = definition.Attributes.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        var usedCommands = new HashSet<string>(StringComparer.Ordinal);
        var commands = definition.Commands.Where(c => !c.Sensitive).ToDictionary(c => c.Name, StringComparer.Ordinal);
        foreach (var attribute in definition.Attributes.Where(a => !a.Sensitive).OrderBy(a => a.Name, StringComparer.Ordinal))
        {
            if (IsAuxiliary(attribute.Name, names))
            {
                continue;
            }

            var state = Attr(capabilityId, attribute.Name);
            var range = Attr(capabilityId, attribute.Name + "Range");
            var (schema, conversion) = ValueSchemaFor(attribute.Value, state?.Unit ?? attribute.DefaultUnit, range);
            if (schema is null)
            {
                continue;
            }

            // A setter with exactly one argument makes the attribute writable; so does a value → command table.
            CapabilityCommandDocument? setter = null;
            ValueSchema? argumentSchema = null;
            if (!readOnly && attribute.Setter is { } setterName && commands.TryGetValue(setterName, out var candidate)
                && candidate.Arguments.Count == 1 && ArgumentSchemaFor(candidate.Arguments[0].Schema, conversion) is { } given)
            {
                setter = candidate;
                argumentSchema = SameKind(given, schema) ? schema : given;
            }

            var enumCommands = !readOnly && setter is null && attribute.EnumCommands.Count > 0
                && attribute.EnumCommands.All(c => commands.TryGetValue(c.Command, out var command) && command.Arguments.All(a => a.Optional))
                    ? attribute.EnumCommands
                    : null;
            var key = Builder.AddGenericAttribute(Naming.GenericKey(capabilityId, attribute.Name), schema, writable: setter is not null || enumCommands is not null);
            Report(CapabilityIds.Generic, key, GenericValue(state, schema, conversion), state?.Timestamp);

            if (setter is not null && argumentSchema is not null)
            {
                var name = Builder.AddGenericCommand(setter.Name, [new ArgumentDefinition(key, argumentSchema, Required: true, Attribute: key)], ConfirmationMode.AwaitState);
                Plan.Generic[name] = new GenericCommandPlan(capabilityId, setter.Name, [new GenericArgumentPlan(key, Optional: false, conversion)], null);
                usedCommands.Add(setter.Name);
            }
            else if (enumCommands is not null)
            {
                var values = enumCommands.Select(c => c.Value).Distinct(StringComparer.Ordinal).ToList();
                var name = Builder.AddGenericCommand("set" + Naming.Pascal(key), [new ArgumentDefinition(key, new EnumSchema(values), Required: true, Attribute: key)], ConfirmationMode.AwaitState);
                Plan.Generic[name] = new GenericCommandPlan(
                    capabilityId,
                    null,
                    [],
                    enumCommands.GroupBy(c => c.Value, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Command, StringComparer.Ordinal));
                usedCommands.UnionWith(enumCommands.Select(c => c.Command));
            }
        }

        if (readOnly)
        {
            return;
        }

        // The remaining commands are actions (no state to wait for).
        foreach (var command in definition.Commands.Where(c => !c.Sensitive && !usedCommands.Contains(c.Name)).OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            var arguments = new List<ArgumentDefinition>();
            var plans = new List<GenericArgumentPlan>();
            var argumentKeys = new HashSet<string>(StringComparer.Ordinal);
            var usable = true;
            foreach (var argument in command.Arguments)
            {
                if (ArgumentSchemaFor(argument.Schema, UnitConversion.None) is not { } schema)
                {
                    usable = false;
                    break;
                }

                var argumentKey = Naming.Unique(Naming.CamelCase(argument.Name), argumentKeys);
                arguments.Add(new ArgumentDefinition(argumentKey, schema, Required: !argument.Optional));
                plans.Add(new GenericArgumentPlan(argumentKey, argument.Optional, UnitConversion.None));
            }

            if (!usable)
            {
                continue;
            }

            var name = Builder.AddGenericCommand(command.Name, arguments, ConfirmationMode.AckIsFinal);
            Plan.Generic[name] = new GenericCommandPlan(capabilityId, command.Name, plans, null);
        }
    }

    /// <summary>Attributes inferred from their current values (no definition): read-only.</summary>
    private void Inferred(string capabilityId)
    {
        var attributes = _status.Of(_component.Id, capabilityId);
        var names = attributes.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var (name, state) in attributes.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            if (IsAuxiliary(name, names) || !state.HasValue)
            {
                continue;
            }

            var (unit, conversion) = StUnits.Canonical(state.Unit);
            ValueSchema schema = state.Value.ValueKind switch
            {
                JsonValueKind.True or JsonValueKind.False => BoolSchema.Instance,
                JsonValueKind.Number => new NumberSchema(null, null, null, unit),
                JsonValueKind.String => new StringSchema(),
                _ => JsonValueSchema.Instance,
            };
            var key = Builder.AddGenericAttribute(Naming.GenericKey(capabilityId, name), schema, writable: false);
            Report(CapabilityIds.Generic, key, GenericValue(state, schema, conversion), state.Timestamp);
        }
    }

    /// <summary><c>supportedX</c>/<c>availableX</c> option lists, and <c>XRange</c> when <c>X</c> exists.</summary>
    private static bool IsAuxiliary(string name, IReadOnlySet<string> siblings) =>
        (name.StartsWith("supported", StringComparison.Ordinal) && name.Length > 9 && char.IsUpper(name[9]))
        || (name.StartsWith("available", StringComparison.Ordinal) && name.Length > 9 && char.IsUpper(name[9]))
        || (name.EndsWith("Range", StringComparison.Ordinal) && siblings.Contains(name[..^5]));

    /// <summary>
    /// A value schema (in the canonical unit) for a definition's JSON schema of <c>value</c>, and the conversion from the
    /// vendor's <paramref name="unit"/>; a <paramref name="range"/> (<c>{minimum, maximum, step}</c>) narrows numbers.
    /// </summary>
    private static (ValueSchema? Schema, UnitConversion Conversion) ValueSchemaFor(JsonElement value, string? unit, StAttribute? range)
    {
        var (canonicalUnit, conversion) = StUnits.Canonical(unit);
        var type = value.Str("type") ?? RefType(value.Str("$ref"));
        switch (type)
        {
            case "string":
                var values = Json.Strings(value.Get("enum"));
                return (values.Count > 0 ? new EnumSchema(values) : new StringSchema(value.Long("maxLength") is { } max and > 0 and <= 4096 ? (int)max : null), UnitConversion.None);
            case "integer":
            case "number":
            {
                double? min = value.Num("minimum");
                double? maxValue = value.Num("maximum");
                double? step = type == "integer" && conversion == UnitConversion.None ? 1 : null;
                if (range is { HasValue: true, Value.ValueKind: JsonValueKind.Object } r)
                {
                    min = r.Value.Num("minimum") ?? min;
                    maxValue = r.Value.Num("maximum") ?? maxValue;
                    if (conversion == UnitConversion.None && r.Value.Num("step") is { } s and > 0)
                    {
                        step = s;
                    }
                }

                min = min is { } a ? StUnits.ToCanonical(a, conversion) : null;
                maxValue = maxValue is { } b ? StUnits.ToCanonical(b, conversion) : null;
                if (min > maxValue)
                {
                    (min, maxValue) = (null, null);
                }

                return (new NumberSchema(min, maxValue, step, canonicalUnit, Integer: type == "integer" && conversion == UnitConversion.None), conversion);
            }

            case "boolean":
                return (BoolSchema.Instance, UnitConversion.None);
            case "object":
            case "array":
            case "json":
                return (JsonValueSchema.Instance, UnitConversion.None);
            default:
                return value.Get("enum") is { } bare && Json.Strings(bare) is { Count: > 0 } items
                    ? (new EnumSchema(items), UnitConversion.None)
                    : (JsonValueSchema.Instance, UnitConversion.None);
        }
    }

    /// <summary>A command argument's schema, or <c>null</c> when it cannot be entered as a simple value.</summary>
    private static ValueSchema? ArgumentSchemaFor(JsonElement schema, UnitConversion conversion)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var type = schema.Str("type") ?? RefType(schema.Str("$ref"));
        return type switch
        {
            "string" => Json.Strings(schema.Get("enum")) is { Count: > 0 } values ? new EnumSchema(values) : new StringSchema(255),
            "integer" when conversion == UnitConversion.None => new NumberSchema(schema.Num("minimum"), schema.Num("maximum"), 1, null, Integer: true),
            "integer" or "number" => new NumberSchema(
                schema.Num("minimum") is { } min ? StUnits.ToCanonical(min, conversion) : null,
                schema.Num("maximum") is { } max ? StUnits.ToCanonical(max, conversion) : null),
            "boolean" => BoolSchema.Instance,
            _ => null,
        };
    }

    private static bool SameKind(ValueSchema a, ValueSchema b) => a.GetType() == b.GetType();

    /// <summary>The JSON type behind SmartThings' shared schema references (<c>#/definitions/IntegerPercent</c>, …).</summary>
    private static string? RefType(string? reference) => reference?.Split('/').LastOrDefault() switch
    {
        null => null,
        "IntegerPercent" or "PositiveInteger" or "Integer" => "integer",
        "PositiveNumber" or "Number" or "Temperature" or "TemperatureValue" => "number",
        "String" or "Iso8601Date" or "URL" or "Uri" => "string",
        "Bool" or "Boolean" => "boolean",
        "JsonObject" or "Object" => "object",
        _ => null,
    };

    /// <summary>A generic attribute's state value, checked against its schema (anything that does not fit is unknown).</summary>
    private static StateValue GenericValue(StAttribute? state, ValueSchema schema, UnitConversion conversion)
    {
        if (state is not { HasValue: true })
        {
            return StateValue.Null;
        }

        return schema switch
        {
            BoolSchema => Json.AsBool(state.Value) is { } flag ? StateValue.Bool(flag) : StateValue.Null,
            NumberSchema number => state.Number is { } value
                ? StateValue.Number(number.Integer ? Math.Round(StUnits.ToCanonical(value, conversion)) : StUnits.ToCanonical(value, conversion))
                : StateValue.Null,
            EnumSchema options => state.Text is { } text && options.Values.Contains(text, StringComparer.Ordinal) ? StateValue.String(text) : StateValue.Null,
            StringSchema text => state.Value.ValueKind switch
            {
                JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                    => StateValue.String(Truncate(state.Text ?? string.Empty, text.MaxLength)),
                _ => StateValue.String(Truncate(state.Value.GetRawText(), text.MaxLength)),
            },
            _ => state.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? StateValue.Json(state.Value) : StateValue.FromJson(state.Value),
        };
    }

    private static string Truncate(string value, int? maxLength) => maxLength is { } max && value.Length > max ? value[..max] : value;
}

using System.Text.Json;

namespace ControlHome.Connectors.SmartThings.Model;

/// <summary>An attribute of a SmartThings capability definition (<c>GET /v1/capabilities/{id}/{version}</c>).</summary>
/// <param name="Value">The JSON schema of <c>value</c> (<c>{"type":"integer","minimum":0}</c>, <c>{"type":"string","enum":[…]}</c>).</param>
/// <param name="DefaultUnit">The unit's default (or its first allowed value).</param>
/// <param name="Setter">The command that sets the attribute, if any.</param>
/// <param name="EnumCommands">Commands that set the attribute to a value, without arguments (<c>on</c> → <c>on</c>).</param>
internal sealed record CapabilityAttributeDocument(
    string Name,
    JsonElement Value,
    string? DefaultUnit,
    bool Sensitive,
    string? Setter,
    IReadOnlyList<(string Command, string Value)> EnumCommands);

/// <summary>An argument of a capability command (positional in <c>POST …/commands</c>).</summary>
internal sealed record CapabilityArgumentDocument(string Name, bool Optional, JsonElement Schema);

/// <summary>A command of a capability definition.</summary>
internal sealed record CapabilityCommandDocument(string Name, IReadOnlyList<CapabilityArgumentDocument> Arguments, bool Sensitive);

/// <summary>A SmartThings capability definition: its attributes' value schemas and its commands' arguments.</summary>
internal sealed record CapabilityDocument(
    string Id,
    int Version,
    string? Name,
    IReadOnlyList<CapabilityAttributeDocument> Attributes,
    IReadOnlyList<CapabilityCommandDocument> Commands)
{
    /// <summary>Parses a definition; <c>null</c> when it is not one.</summary>
    public static CapabilityDocument? Parse(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object || document.Str("id") is not { Length: > 0 } id)
        {
            return null;
        }

        var attributes = new List<CapabilityAttributeDocument>();
        foreach (var attribute in document.Members("attributes"))
        {
            if (attribute.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var schema = attribute.Value.Get("schema");
            var properties = schema.Get("properties");
            if (properties.Get("value") is not { ValueKind: JsonValueKind.Object } value)
            {
                continue;
            }

            var unit = properties.Get("unit");
            var defaultUnit = unit.Str("default") ?? (unit.Get("enum") is { } units ? Json.Strings(units).FirstOrDefault() : null);
            var enumCommands = new List<(string Command, string Value)>();
            foreach (var enumCommand in attribute.Value.Array("enumCommands"))
            {
                if (enumCommand.Str("command") is { Length: > 0 } command && enumCommand.Str("value") is { } target)
                {
                    enumCommands.Add((command, target));
                }
            }

            attributes.Add(new CapabilityAttributeDocument(
                attribute.Name,
                value.Clone(),
                defaultUnit,
                schema is { } s && s.Bool("sensitive") == true,
                attribute.Value.Str("setter"),
                enumCommands));
        }

        var commands = new List<CapabilityCommandDocument>();
        foreach (var command in document.Members("commands"))
        {
            if (command.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var arguments = command.Value.Array("arguments")
                .Select(a => a.Str("name") is { Length: > 0 } name
                    ? new CapabilityArgumentDocument(name, a.Bool("optional") == true, a.Get("schema")?.Clone() ?? default)
                    : null)
                .OfType<CapabilityArgumentDocument>()
                .ToList();
            commands.Add(new CapabilityCommandDocument(command.Value.Str("name") ?? command.Name, arguments, command.Value.Bool("sensitive") == true));
        }

        return new CapabilityDocument(id, (int)(document.Long("version") ?? 1), document.Str("name"), attributes, commands);
    }
}

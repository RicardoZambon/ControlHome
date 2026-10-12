using ControlHome.Domain.Capabilities;

namespace ControlHome.Application.Capabilities;

/// <summary>A capability definition as served by <c>GET /api/capabilities</c> (and inside generic endpoint capabilities).</summary>
/// <param name="Id">e.g. <c>thermostat</c>.</param>
/// <param name="Version">Catalogue version.</param>
public sealed record CapabilityDto(
    string Id,
    int Version,
    IReadOnlyList<AttributeDefinitionDto> Attributes,
    IReadOnlyList<CommandDefinitionDto> Commands);

/// <summary>An attribute of a capability.</summary>
/// <param name="Schema">The value's shape (discriminator <c>type</c>: bool, number, enum, string, flags, json, momentary).</param>
/// <param name="Unit">Canonical unit (<c>%</c>, <c>W</c>, <c>Wh</c>, <c>°C</c>, …), if any.</param>
/// <param name="StateClass"><c>none</c>, <c>measurement</c>, <c>total</c> or <c>total_increasing</c>.</param>
/// <param name="Category"><c>primary</c> or <c>diagnostic</c>.</param>
/// <param name="Recorded">Whether changes are kept in the history.</param>
/// <param name="Writable">Whether a command of the capability changes it.</param>
public sealed record AttributeDefinitionDto(
    string Key,
    ValueSchema Schema,
    string? Unit,
    string StateClass,
    string Category,
    bool Recorded,
    bool Writable);

/// <summary>A command of a capability.</summary>
/// <param name="Confirmation"><c>ackIsFinal</c> or <c>awaitState</c>.</param>
/// <param name="TimeoutMs">Confirmation timeout; <c>null</c> uses the connector's default.</param>
/// <param name="SafetyCritical">Always needs a step-up PIN (unlock, door opener, disarm).</param>
/// <param name="SafetyCriticalForDeviceClasses">Device classes on which it needs a step-up PIN too (cover open on gate/garage).</param>
/// <param name="FailSafe">The safe direction (lock, close): never gated.</param>
/// <param name="GateAttribute">A boolean attribute that must be true for the command to be accepted, if any.</param>
public sealed record CommandDefinitionDto(
    string Name,
    IReadOnlyList<ArgumentDefinitionDto> Arguments,
    string Confirmation,
    int? TimeoutMs,
    bool SafetyCritical,
    IReadOnlyList<string> SafetyCriticalForDeviceClasses,
    bool FailSafe,
    string? GateAttribute);

/// <summary>An argument of a command (a member of the command's <c>arguments</c> object).</summary>
/// <param name="Attribute">The attribute it sets (its constraints apply), if any.</param>
public sealed record ArgumentDefinitionDto(string Key, ValueSchema Schema, bool Required, string? Attribute);

/// <summary>Hand mapping from the capability model to its DTOs.</summary>
public static class CapabilityMapping
{
    /// <summary>The whole catalogue, mapped once.</summary>
    public static IReadOnlyList<CapabilityDto> Catalog { get; } = CapabilityCatalog.All.Select(ToDto).ToList();

    public static CapabilityDto ToDto(CapabilityDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new CapabilityDto(
            definition.Id,
            definition.Version,
            definition.Attributes.Select(ToDto).ToList(),
            definition.Commands.Select(ToDto).ToList());
    }

    public static string ToText(StateClass stateClass) => stateClass switch
    {
        StateClass.Measurement => "measurement",
        StateClass.Total => "total",
        StateClass.TotalIncreasing => "total_increasing",
        _ => "none",
    };

    public static string ToText(AttributeCategory category) => category == AttributeCategory.Diagnostic ? "diagnostic" : "primary";

    public static string ToText(ConfirmationMode confirmation) => confirmation == ConfirmationMode.AckIsFinal ? "ackIsFinal" : "awaitState";

    private static AttributeDefinitionDto ToDto(AttributeDefinition attribute) => new(
        attribute.Key,
        attribute.Schema,
        attribute.Unit,
        ToText(attribute.StateClass),
        ToText(attribute.Category),
        attribute.Recorded,
        attribute.Writable);

    private static CommandDefinitionDto ToDto(CommandDefinition command) => new(
        command.Name,
        command.Arguments.Select(a => new ArgumentDefinitionDto(a.Key, a.Schema, a.Required, a.Attribute)).ToList(),
        ToText(command.Confirmation),
        command.Timeout is { } timeout ? (int)timeout.TotalMilliseconds : null,
        command.SafetyCritical,
        command.SafetyCriticalForDeviceClasses,
        command.FailSafe,
        command.GateAttribute);
}

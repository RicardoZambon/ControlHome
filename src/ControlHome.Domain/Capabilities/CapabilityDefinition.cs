namespace ControlHome.Domain.Capabilities;

/// <summary>
/// A versioned set of attributes and commands an endpoint can have (e.g. <c>switch</c>: attribute <c>on</c>, commands
/// <c>turnOn</c>, <c>turnOff</c>, <c>toggle</c>). Defined in code in <see cref="CapabilityCatalog"/>; the <c>generic</c>
/// capability's definition is supplied per endpoint by the connector instead (stored as JSON with
/// <see cref="CapabilityJson.Options"/>).
/// </summary>
/// <param name="Id">camelCase, unique (see <see cref="CapabilityIds"/>).</param>
/// <param name="Version">Bumped on incompatible changes; endpoints remember the version they were created with.</param>
public sealed record CapabilityDefinition(
    string Id,
    int Version,
    IReadOnlyList<AttributeDefinition> Attributes,
    IReadOnlyList<CommandDefinition> Commands)
{
    /// <summary>The attribute named <paramref name="key"/>, or <c>null</c>.</summary>
    public AttributeDefinition? FindAttribute(string key) =>
        Attributes.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal));

    /// <summary>The command named <paramref name="name"/>, or <c>null</c>.</summary>
    public CommandDefinition? FindCommand(string name) =>
        Commands.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// The command that sets attribute <paramref name="attributeKey"/> on its own: its only required argument is bound to
    /// the attribute (<see cref="ArgumentDefinition.Attribute"/>, or an argument with the attribute's key). <c>null</c> if
    /// there is none.
    /// </summary>
    public CommandDefinition? FindSetter(string attributeKey) =>
        Commands.FirstOrDefault(c =>
            c.Arguments.Count(a => a.Required) == 1
            && c.Arguments.Single(a => a.Required) is var argument
            && string.Equals(argument.Attribute ?? argument.Key, attributeKey, StringComparison.Ordinal));

    public bool Equals(CapabilityDefinition? other) =>
        other is not null
        && string.Equals(Id, other.Id, StringComparison.Ordinal)
        && Version == other.Version
        && Attributes.SequenceEqual(other.Attributes)
        && Commands.SequenceEqual(other.Commands);

    public override int GetHashCode() => HashCode.Combine(Id, Version, Attributes.Count, Commands.Count);
}

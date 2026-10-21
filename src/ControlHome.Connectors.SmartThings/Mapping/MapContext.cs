using ControlHome.Connectors.SmartThings.Model;

namespace ControlHome.Connectors.SmartThings.Mapping;

/// <summary>Whether a capability definition is loaded.</summary>
internal enum DefinitionState
{
    /// <summary>Not fetched yet: the capability is left out until it is (no guessing that would change later).</summary>
    Pending,

    Known,

    /// <summary>It cannot be fetched (custom namespace, not found): attributes are inferred from their values, read-only.</summary>
    Unavailable,
}

/// <summary>The answer of a capability-definition lookup.</summary>
internal sealed record DefinitionLookup(DefinitionState State, CapabilityDocument? Document)
{
    public static DefinitionLookup Pending { get; } = new(DefinitionState.Pending, null);

    public static DefinitionLookup Unavailable { get; } = new(DefinitionState.Unavailable, null);

    public static DefinitionLookup Of(CapabilityDocument document) => new(DefinitionState.Known, document);
}

/// <summary>What the mapper needs besides the device itself.</summary>
/// <param name="NowUtc">The connector clock's now (vendor timestamps are never later; remaining times count from it).</param>
/// <param name="Rooms">SmartThings room id → name (suggested rooms).</param>
/// <param name="Definitions">Capability definitions for capabilities without a catalogue mapping.</param>
/// <param name="KnownDeviceIds">The ids in the same inventory (a parent outside it is dropped).</param>
internal sealed record MapContext(
    DateTimeOffset NowUtc,
    IReadOnlyDictionary<string, string> Rooms,
    Func<CapabilityRef, DefinitionLookup> Definitions,
    IReadOnlySet<string>? KnownDeviceIds = null)
{
    public static MapContext At(DateTimeOffset nowUtc) =>
        new(nowUtc, new Dictionary<string, string>(), _ => DefinitionLookup.Unavailable);
}

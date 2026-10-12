using System.Text.Json;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Connectors.Abstractions;

/// <summary>A device as the connector sees it (the inventory merge turns it into a <c>Device</c> with endpoints).</summary>
/// <param name="ExternalId">Stable vendor id, unique within the integration — never an IP address.</param>
/// <param name="Name">The vendor's name for the device; used only when the device is first imported.</param>
/// <param name="DeviceClass">A <c>DeviceClasses</c> value (e.g. <c>gate</c> makes <c>cover.open</c> safety-critical).</param>
/// <param name="ParentExternalId">The device it is reached through (an NVR for a camera), in the same integration.</param>
/// <param name="Connections">Hardware identifiers (<c>mac</c>, <c>serial</c>, <c>matter</c>, …) used to spot duplicates.</param>
/// <param name="SuggestedRoom">A room name from the vendor app, offered when importing (matched by name).</param>
/// <param name="Endpoints">At least one; a single-endpoint device uses the key <c>main</c>.</param>
public sealed record DiscoveredDevice(
    string ExternalId,
    string Name,
    string? DeviceClass,
    string? Manufacturer,
    string? Model,
    string? Firmware,
    string? ParentExternalId,
    IReadOnlyDictionary<string, string> Connections,
    string? SuggestedRoom,
    IReadOnlyList<DiscoveredEndpoint> Endpoints);

/// <summary>One endpoint of a discovered device.</summary>
/// <param name="Key">Unique within the device (e.g. <c>main</c>, <c>l1</c>).</param>
/// <param name="Name">The vendor's name for the endpoint (e.g. a channel label), or <c>null</c>.</param>
/// <param name="Capabilities">Each capability id at most once.</param>
public sealed record DiscoveredEndpoint(string Key, string? Name, IReadOnlyList<CapabilityBinding> Capabilities);

/// <summary>A capability an endpoint has.</summary>
/// <param name="CapabilityId">A catalogue id (see <c>CapabilityIds</c>).</param>
/// <param name="Constraints">The endpoint's narrowing as a <c>CapabilityConstraints</c> JSON object, or <c>null</c>.</param>
/// <param name="GenericDefinition">
/// Required for <c>generic</c> (its id must be <c>generic</c>), <c>null</c> for every other capability.
/// </param>
public sealed record CapabilityBinding(string CapabilityId, JsonElement? Constraints, CapabilityDefinition? GenericDefinition)
{
    /// <summary>A catalogue capability with its full ranges.</summary>
    public static CapabilityBinding Of(string capabilityId) => new(capabilityId, null, null);

    /// <summary>A catalogue capability narrowed by <paramref name="constraints"/> (serialized with <c>CapabilityJson</c>).</summary>
    public static CapabilityBinding Of(string capabilityId, CapabilityConstraints constraints) =>
        new(capabilityId, JsonSerializer.SerializeToElement(constraints, CapabilityJson.Options), null);
}

using System.Text.Json;
using ControlHome.Application.Capabilities;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Application.Devices;

/// <summary>Filters of <c>GET /api/devices</c> (all optional, combined with AND).</summary>
/// <param name="RoomId">Devices with at least one endpoint in the room (an endpoint's own room, else the device's).</param>
/// <param name="IntegrationId">Devices of one integration.</param>
/// <param name="Capability">Devices with an endpoint that has this (non-retired) capability, e.g. <c>switch</c>.</param>
/// <param name="Q">Text contained in the name, manufacturer or model.</param>
public sealed record DeviceListQuery(int? RoomId = null, Guid? IntegrationId = null, string? Capability = null, string? Q = null);

/// <summary>A device in the device list, with its endpoints, capabilities, attribute ids and current values.</summary>
/// <param name="ParentDeviceId">The device it is reached through (an NVR for a camera), if any.</param>
/// <param name="Availability"><c>Unknown</c>, <c>Online</c> or <c>Offline</c>.</param>
/// <param name="IsOrphaned">No longer reported by its integration (kept, shown with a badge).</param>
/// <param name="Endpoints">In display order.</param>
public sealed record DeviceSummaryDto(
    Guid Id,
    Guid IntegrationId,
    Guid? ParentDeviceId,
    string Name,
    string? DeviceClass,
    string? Icon,
    int? RoomId,
    string Availability,
    bool IsOrphaned,
    DateTimeOffset? LastSeenAtUtc,
    IReadOnlyList<EndpointDto> Endpoints);

/// <summary>A device with its vendor details (<c>GET /api/devices/{id}</c>).</summary>
/// <param name="ConnectorKey">The integration's connector, e.g. <c>simulated</c>.</param>
/// <param name="ExternalId">The connector's id for the device.</param>
/// <param name="Connections">Hardware identifiers (<c>mac</c>, <c>serial</c>, …).</param>
/// <param name="Children">Devices reached through this one (e.g. an NVR's cameras), by name.</param>
public sealed record DeviceDetailDto(
    Guid Id,
    Guid IntegrationId,
    string IntegrationName,
    string ConnectorKey,
    string ExternalId,
    Guid? ParentDeviceId,
    string Name,
    string? DeviceClass,
    string? Manufacturer,
    string? Model,
    string? Firmware,
    IReadOnlyDictionary<string, string> Connections,
    string Availability,
    DateTimeOffset? LastSeenAtUtc,
    bool IsOrphaned,
    int? RoomId,
    string? Icon,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<EndpointDto> Endpoints,
    IReadOnlyList<DeviceChildDto> Children);

/// <summary>A child device of a <see cref="DeviceDetailDto"/>.</summary>
public sealed record DeviceChildDto(Guid Id, string Name);

/// <summary>A controllable part of a device (commands address endpoints).</summary>
/// <param name="Key">The connector's key (<c>main</c> for single-endpoint devices).</param>
/// <param name="Name">The user's name, or <c>null</c> to show the device name.</param>
/// <param name="RoomId">The endpoint's own room, or <c>null</c> to inherit the device's.</param>
/// <param name="EffectiveRoomId">The room the endpoint is in: its own, else the device's.</param>
/// <param name="Hidden">Left out of dashboards and pickers.</param>
/// <param name="Favorite">On the dashboard's favorites grid.</param>
public sealed record EndpointDto(
    Guid Id,
    string Key,
    string? Name,
    int? RoomId,
    int? EffectiveRoomId,
    string? Icon,
    bool Hidden,
    bool Favorite,
    int SortOrder,
    IReadOnlyList<EndpointCapabilityDto> Capabilities);

/// <summary>A capability of an endpoint with its constraints and attributes.</summary>
/// <param name="Id">The endpoint capability's id.</param>
/// <param name="CapabilityId">A catalogue id (definitions: <c>GET /api/capabilities</c>).</param>
/// <param name="Retired">No longer reported by the connector: hide its controls (history is kept).</param>
/// <param name="Constraints">The endpoint's narrowing (ranges, enum subsets) as a constraints object, or <c>null</c>.</param>
/// <param name="GenericDefinition">For <c>generic</c>: the connector-supplied definition; otherwise <c>null</c>.</param>
/// <param name="Attributes">The attributes known so far (ordered by key).</param>
public sealed record EndpointCapabilityDto(
    int Id,
    string CapabilityId,
    int CapabilityVersion,
    bool Retired,
    JsonElement? Constraints,
    CapabilityDto? GenericDefinition,
    IReadOnlyList<AttributeDto> Attributes);

/// <summary>One attribute and its current value.</summary>
/// <param name="AttributeId">The key of every state, history and hub delta for this attribute.</param>
/// <param name="Value">The current value; <c>{kind:'null'}</c> when none was reported yet.</param>
/// <param name="ObservedAtUtc">When the value was observed, or <c>null</c> when none was reported yet.</param>
/// <param name="LastChangedAtUtc">When the value last changed, or <c>null</c>.</param>
/// <param name="Restored">Loaded from the database and not yet confirmed by the running connector.</param>
public sealed record AttributeDto(
    int AttributeId,
    string Key,
    StateValue Value,
    DateTimeOffset? ObservedAtUtc,
    DateTimeOffset? LastChangedAtUtc,
    bool Restored);

/// <summary>The current value of one attribute (<c>GET /api/state</c> resync, and the source of <see cref="AttributeDto"/>).</summary>
/// <param name="Restored">Loaded from the database and not yet confirmed by the running connector.</param>
public sealed record AttributeStateDto(
    int AttributeId,
    StateValue Value,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset LastChangedAtUtc,
    bool Restored);

/// <summary>An integration in the integrations list.</summary>
/// <param name="ConnectorKey">e.g. <c>simulated</c>.</param>
/// <param name="Status">
/// <c>Connecting</c>, <c>Ok</c>, <c>Degraded</c>, <c>NeedsReauth</c>, <c>PlanRequired</c>, <c>QuotaExceeded</c>,
/// <c>Error</c> or <c>Stopped</c>.
/// </param>
/// <param name="StatusMessage">A short non-secret explanation of the status, if any.</param>
public sealed record IntegrationDto(
    Guid Id,
    string ConnectorKey,
    string Name,
    string Status,
    string? StatusMessage,
    bool Enabled,
    int DeviceCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

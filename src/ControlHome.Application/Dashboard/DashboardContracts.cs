using ControlHome.Application.Devices;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Application.Dashboard;

/// <summary>
/// The home at a glance (<c>GET /api/dashboard</c>): favorites, rooms with what is on / the temperature / the power drawn,
/// the power drawn now, offline devices and integration health. Values are current when served; to keep them live, apply
/// hub <c>StateChanged</c> deltas to the listed attribute ids, and refetch on <c>DevicesChanged</c>.
/// </summary>
/// <param name="FavoriteEndpointIds">Favorite endpoints (not hidden) in display order: render them with the device tiles.</param>
/// <param name="Rooms">Every room of every floor, by floor level and room order (empty: point to the floor editor).</param>
/// <param name="Power">What the home draws now.</param>
/// <param name="OfflineDevices">Devices their connector reports as unreachable (orphaned devices are left out).</param>
/// <param name="Integrations">Every integration with its status (empty: point to the integrations page).</param>
/// <param name="DeviceCount">Devices that are not orphaned.</param>
public sealed record DashboardDto(
    IReadOnlyList<Guid> FavoriteEndpointIds,
    IReadOnlyList<DashboardRoomDto> Rooms,
    DashboardPowerDto Power,
    IReadOnlyList<DashboardDeviceDto> OfflineDevices,
    IReadOnlyList<IntegrationDto> Integrations,
    int FloorCount,
    int DeviceCount);

/// <summary>One room's summary. Endpoints count in their own room, else their device's; hidden endpoints are left out.</summary>
/// <param name="DeviceCount">Devices with at least one endpoint in the room.</param>
/// <param name="ActiveDeviceCount">
/// Devices with something on in the room: a switch on, a media player powered, an appliance running (see
/// <paramref name="ActivityAttributes"/>).
/// </param>
/// <param name="TemperatureC">The mean of the room's temperature readings (°C, one decimal), or <c>null</c> when it has none.</param>
/// <param name="PowerW">The sum of the room's power readings (W, one decimal), or <c>null</c> when it has none.</param>
/// <param name="TemperatureAttributeIds">The temperature readings averaged into <paramref name="TemperatureC"/>.</param>
/// <param name="PowerAttributeIds">The power readings summed into <paramref name="PowerW"/>.</param>
/// <param name="ActivityAttributes">What makes a device count as active, to recount from live values.</param>
public sealed record DashboardRoomDto(
    int Id,
    string Name,
    string Icon,
    string ColorToken,
    int FloorId,
    string FloorName,
    int DeviceCount,
    int ActiveDeviceCount,
    double? TemperatureC,
    double? PowerW,
    IReadOnlyList<int> TemperatureAttributeIds,
    IReadOnlyList<int> PowerAttributeIds,
    IReadOnlyList<DashboardActivityDto> ActivityAttributes);

/// <summary>A device counts as active while this attribute has one of <paramref name="ActiveValues"/>.</summary>
public sealed record DashboardActivityDto(Guid DeviceId, int AttributeId, IReadOnlyList<StateValue> ActiveValues);

/// <summary>The power drawn now: the sum of every <c>power.power</c> reading (W, one decimal; <c>null</c> without readings).</summary>
public sealed record DashboardPowerDto(double? Watts, IReadOnlyList<int> AttributeIds);

/// <summary>A device in a dashboard list.</summary>
/// <param name="Availability"><c>Unknown</c>, <c>Online</c> or <c>Offline</c>.</param>
public sealed record DashboardDeviceDto(Guid Id, string Name, string? DeviceClass, int? RoomId, string Availability, DateTimeOffset? LastSeenAtUtc);

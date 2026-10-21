using ControlHome.Application.Abstractions;
using ControlHome.Application.Devices;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Application.Dashboard;

/// <summary>Builds the dashboard summary (<see cref="DashboardDto"/>) from the device model and the current values.</summary>
public sealed class DashboardService(
    IControlHomeDb db,
    IAttributeStateReader states,
    IDeviceStatusReader statuses,
    DeviceQueryService queries)
{
    /// <summary>What makes an endpoint active: (capability, attribute) → the values that count as "on".</summary>
    private static readonly (string Capability, string Attribute, StateValue[] Active)[] _activity =
    [
        (CapabilityIds.Switch, "on", [StateValue.True]),
        (CapabilityIds.MediaPlayer, "power", [StateValue.True]),
        (CapabilityIds.ApplianceCycle, "machineState", [StateValue.String("running")]),
    ];

    public async Task<DashboardDto> GetAsync(CancellationToken cancellationToken)
    {
        var rooms = await (from room in db.Rooms.AsNoTracking()
                           join floor in db.Floors.AsNoTracking() on room.FloorId equals floor.Id
                           orderby floor.Level, floor.Id, room.SortOrder, room.Id
                           select new { room.Id, room.Name, room.Icon, room.ColorToken, FloorId = floor.Id, FloorName = floor.Name })
            .ToListAsync(cancellationToken);
        var floorCount = await db.Floors.CountAsync(cancellationToken);
        var devices = await db.Devices.AsNoTracking()
            .Where(d => !d.IsOrphaned)
            .Include(d => d.Endpoints)
            .ThenInclude(e => e.Capabilities)
            .ThenInclude(c => c.Attributes)
            .OrderBy(d => d.Name)
            .ThenBy(d => d.Id)
            .ToListAsync(cancellationToken);

        var endpoints = devices
            .SelectMany(d => d.Endpoints.OrderBy(e => e.SortOrder).ThenBy(e => e.Key, StringComparer.Ordinal).Select(e => (Device: d, Endpoint: e)))
            .ToList();
        var values = await states.GetAsync([.. endpoints.SelectMany(x => x.Endpoint.Capabilities).SelectMany(c => c.Attributes).Select(a => a.Id)], cancellationToken);
        var live = statuses.Get([.. devices.Select(d => d.Id)]);

        var power = endpoints.SelectMany(x => AttributeIds(x.Endpoint, CapabilityIds.Power, "power")).ToList();
        var roomDtos = rooms.Select(room =>
            {
                var inRoom = endpoints.Where(x => !x.Endpoint.Hidden && (x.Endpoint.RoomId ?? x.Device.RoomId) == room.Id).ToList();
                var temperature = inRoom.SelectMany(x => TemperatureIds(x.Endpoint)).ToList();
                var roomPower = inRoom.SelectMany(x => AttributeIds(x.Endpoint, CapabilityIds.Power, "power")).ToList();
                var activity = inRoom
                    .SelectMany(x => _activity.SelectMany(a => AttributeIds(x.Endpoint, a.Capability, a.Attribute)
                        .Select(id => new DashboardActivityDto(x.Device.Id, id, a.Active))))
                    .ToList();
                var active = activity
                    .Where(a => values.TryGetValue(a.AttributeId, out var state) && a.ActiveValues.Contains(state.Value))
                    .Select(a => a.DeviceId)
                    .Distinct()
                    .Count();
                return new DashboardRoomDto(
                    room.Id,
                    room.Name,
                    room.Icon,
                    room.ColorToken,
                    room.FloorId,
                    room.FloorName,
                    inRoom.Select(x => x.Device.Id).Distinct().Count(),
                    active,
                    Mean(temperature, values),
                    Sum(roomPower, values),
                    temperature,
                    roomPower,
                    activity);
            })
            .ToList();

        var offline = devices
            .Select(d => (Device: d, Status: live.TryGetValue(d.Id, out var status) ? status : new DeviceLiveStatus(d.Availability, d.LastSeenAtUtc)))
            .Where(x => x.Status.Availability == Availability.Offline)
            .Select(x => new DashboardDeviceDto(x.Device.Id, x.Device.Name, x.Device.DeviceClass, x.Device.RoomId, x.Status.Availability.ToString(), x.Status.LastSeenAtUtc))
            .ToList();

        return new DashboardDto(
            [.. endpoints.Where(x => x.Endpoint.Favorite && !x.Endpoint.Hidden).Select(x => x.Endpoint.Id)],
            roomDtos,
            new DashboardPowerDto(Sum(power, values), power),
            offline,
            await queries.ListIntegrationsAsync(cancellationToken),
            floorCount,
            devices.Count);
    }

    /// <summary>The ids of an endpoint's (non-retired) capability attribute.</summary>
    private static IEnumerable<int> AttributeIds(Endpoint endpoint, string capabilityId, string attributeKey) =>
        endpoint.Capabilities
            .Where(c => c.CapabilityId == capabilityId && !c.Retired)
            .SelectMany(c => c.Attributes.Where(a => a.Key == attributeKey))
            .Select(a => a.Id);

    /// <summary>The endpoint's room temperature: a temperature sensor, else a thermostat's current temperature.</summary>
    private static IEnumerable<int> TemperatureIds(Endpoint endpoint)
    {
        var sensors = AttributeIds(endpoint, CapabilityIds.Temperature, "temperature").ToList();
        return sensors.Count > 0 ? sensors : AttributeIds(endpoint, CapabilityIds.Thermostat, "currentTemperature");
    }

    private static double? Mean(IReadOnlyList<int> ids, IReadOnlyDictionary<int, AttributeStateDto> values)
    {
        var numbers = Numbers(ids, values);
        return numbers.Count == 0 ? null : Math.Round(numbers.Average(), 1, MidpointRounding.AwayFromZero);
    }

    private static double? Sum(IReadOnlyList<int> ids, IReadOnlyDictionary<int, AttributeStateDto> values)
    {
        var numbers = Numbers(ids, values);
        return numbers.Count == 0 ? null : Math.Round(numbers.Sum(), 1, MidpointRounding.AwayFromZero);
    }

    private static List<double> Numbers(IReadOnlyList<int> ids, IReadOnlyDictionary<int, AttributeStateDto> values) =>
        [.. ids.Select(id => values.TryGetValue(id, out var state) ? state.Value.N : null).OfType<double>()];
}

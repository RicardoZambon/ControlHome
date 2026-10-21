using System.Text.Json;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Capabilities;
using ControlHome.Application.Common;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Application.Devices;

/// <summary>
/// Read side of the device model: the device list and detail with endpoints, capabilities, attribute ids and current
/// values (from <see cref="IAttributeStateReader"/>) and live availability / last-seen (from <see cref="IDeviceStatusReader"/>,
/// so a client resyncing after a hub reconnect never gets the write-behind's older copy), the full state for client
/// resyncs, and the integrations list.
/// </summary>
public sealed class DeviceQueryService(IControlHomeDb db, IAttributeStateReader states, IDeviceStatusReader statuses)
{
    /// <summary>The devices matching <paramref name="query"/>, by name.</summary>
    public async Task<IReadOnlyList<DeviceSummaryDto>> ListAsync(DeviceListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var devices = db.Devices.AsNoTracking();
        if (query.IntegrationId is { } integrationId)
        {
            devices = devices.Where(d => d.IntegrationId == integrationId);
        }

        if (query.RoomId is { } roomId)
        {
            devices = devices.Where(d =>
                d.Endpoints.Any(e => e.RoomId == roomId)
                || (d.RoomId == roomId && (!d.Endpoints.Any() || d.Endpoints.Any(e => e.RoomId == null))));
        }

        if (!string.IsNullOrWhiteSpace(query.Capability))
        {
            var capability = query.Capability.Trim();
            devices = devices.Where(d => d.Endpoints.Any(e => e.Capabilities.Any(c => c.CapabilityId == capability && !c.Retired)));
        }

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var text = query.Q.Trim();
            devices = devices.Where(d =>
                d.Name.Contains(text)
                || (d.Manufacturer != null && d.Manufacturer.Contains(text))
                || (d.Model != null && d.Model.Contains(text)));
        }

        var list = await WithInventory(devices).OrderBy(d => d.Name).ThenBy(d => d.Id).ToListAsync(cancellationToken);
        var values = await ValuesAsync(list, cancellationToken);
        var live = statuses.Get([.. list.Select(d => d.Id)]);
        var duplicates = await DuplicatesAsync(cancellationToken);
        return list.Select(device =>
            {
                var (availability, lastSeen) = StatusOf(device, live);
                return new DeviceSummaryDto(
                    device.Id,
                    device.IntegrationId,
                    device.ParentDeviceId,
                    device.Name,
                    device.DeviceClass,
                    device.Icon,
                    device.RoomId,
                    availability,
                    device.IsOrphaned,
                    lastSeen,
                    Endpoints(device, values),
                    duplicates.GetValueOrDefault(device.Id) ?? []);
            })
            .ToList();
    }

    /// <summary>
    /// For each device that has any: the devices of other integrations sharing a hardware identifier (MAC address or
    /// serial number, see <see cref="DeviceIdentity"/>), by id.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> DuplicatesAsync(CancellationToken cancellationToken)
    {
        var rows = await db.Devices.AsNoTracking()
            .Select(d => new { d.Id, d.IntegrationId, d.ConnectionsJson })
            .ToListAsync(cancellationToken);
        var byKey = new Dictionary<string, List<(Guid Id, Guid IntegrationId)>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var key in DeviceIdentity.HardwareKeys(ParseConnections(row.ConnectionsJson)))
            {
                if (!byKey.TryGetValue(key, out var group))
                {
                    byKey[key] = group = [];
                }

                group.Add((row.Id, row.IntegrationId));
            }
        }

        var result = new Dictionary<Guid, SortedSet<Guid>>();
        foreach (var group in byKey.Values.Where(g => g.Count > 1))
        {
            foreach (var device in group)
            {
                foreach (var other in group.Where(o => o.IntegrationId != device.IntegrationId))
                {
                    if (!result.TryGetValue(device.Id, out var set))
                    {
                        result[device.Id] = set = [];
                    }

                    set.Add(other.Id);
                }
            }
        }

        return result.ToDictionary(r => r.Key, r => (IReadOnlyList<Guid>)[.. r.Value]);
    }

    /// <summary>One device with its vendor details and children; 404 <c>devices.not_found</c>.</summary>
    public async Task<Result<DeviceDetailDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var device = await WithInventory(db.Devices.AsNoTracking()).SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (device is null)
        {
            return AppError.NotFound(ErrorCodes.DevicesNotFound, "No such device.");
        }

        var integration = await db.Integrations.AsNoTracking()
            .Where(i => i.Id == device.IntegrationId)
            .Select(i => new { i.Name, i.ConnectorKey })
            .SingleAsync(cancellationToken);
        var children = await db.Devices.AsNoTracking()
            .Where(d => d.ParentDeviceId == id)
            .OrderBy(d => d.Name)
            .ThenBy(d => d.Id)
            .Select(d => new DeviceChildDto(d.Id, d.Name))
            .ToListAsync(cancellationToken);
        var values = await ValuesAsync([device], cancellationToken);
        var (availability, lastSeen) = StatusOf(device, statuses.Get([device.Id]));
        var duplicates = await DuplicatesAsync(cancellationToken);

        return new DeviceDetailDto(
            device.Id,
            device.IntegrationId,
            integration.Name,
            integration.ConnectorKey,
            device.ExternalId,
            device.ParentDeviceId,
            device.Name,
            device.DeviceClass,
            device.Manufacturer,
            device.Model,
            device.Firmware,
            ParseConnections(device.ConnectionsJson),
            availability,
            lastSeen,
            device.IsOrphaned,
            device.RoomId,
            device.Icon,
            device.CreatedAtUtc,
            Endpoints(device, values),
            children,
            duplicates.GetValueOrDefault(device.Id) ?? []);
    }

    /// <summary>Every current attribute value (<c>GET /api/state</c>).</summary>
    public Task<IReadOnlyList<AttributeStateDto>> GetStatesAsync(CancellationToken cancellationToken) =>
        states.GetAllAsync(cancellationToken);

    /// <summary>All integrations, by name.</summary>
    public async Task<IReadOnlyList<IntegrationDto>> ListIntegrationsAsync(CancellationToken cancellationToken)
    {
        var rows = await db.Integrations.AsNoTracking()
            .OrderBy(i => i.Name)
            .ThenBy(i => i.Id)
            .Select(i => new { Integration = i, DeviceCount = i.Devices.Count })
            .ToListAsync(cancellationToken);
        return rows.Select(r => new IntegrationDto(
                r.Integration.Id,
                r.Integration.ConnectorKey,
                r.Integration.Name,
                r.Integration.Status.ToString(),
                r.Integration.StatusMessage,
                r.Integration.Enabled,
                r.DeviceCount,
                r.Integration.CreatedAtUtc,
                r.Integration.UpdatedAtUtc))
            .ToList();
    }

    /// <summary>The live availability and last-seen when the runtime has them, else the database's.</summary>
    private static (string Availability, DateTimeOffset? LastSeenAtUtc) StatusOf(Device device, IReadOnlyDictionary<Guid, DeviceLiveStatus> live) =>
        live.TryGetValue(device.Id, out var status)
            ? (status.Availability.ToString(), Later(status.LastSeenAtUtc, device.LastSeenAtUtc))
            : (device.Availability.ToString(), device.LastSeenAtUtc);

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null || a > b ? a : b;

    private static IQueryable<Device> WithInventory(IQueryable<Device> devices) =>
        devices
            .Include(d => d.Endpoints)
            .ThenInclude(e => e.Capabilities)
            .ThenInclude(c => c.Attributes);

    private async Task<IReadOnlyDictionary<int, AttributeStateDto>> ValuesAsync(
        IReadOnlyList<Device> devices,
        CancellationToken cancellationToken)
    {
        var attributeIds = devices
            .SelectMany(d => d.Endpoints)
            .SelectMany(e => e.Capabilities)
            .SelectMany(c => c.Attributes)
            .Select(a => a.Id)
            .ToList();
        return await states.GetAsync(attributeIds, cancellationToken);
    }

    private static List<EndpointDto> Endpoints(Device device, IReadOnlyDictionary<int, AttributeStateDto> values) =>
        device.Endpoints
            .OrderBy(e => e.SortOrder)
            .ThenBy(e => e.Key, StringComparer.Ordinal)
            .Select(endpoint => new EndpointDto(
                endpoint.Id,
                endpoint.Key,
                endpoint.Name,
                endpoint.RoomId,
                endpoint.RoomId ?? device.RoomId,
                endpoint.Icon,
                endpoint.Hidden,
                endpoint.Favorite,
                endpoint.SortOrder,
                endpoint.Capabilities
                    .OrderBy(c => c.CapabilityId, StringComparer.Ordinal)
                    .Select(capability => new EndpointCapabilityDto(
                        capability.Id,
                        capability.CapabilityId,
                        capability.CapabilityVersion,
                        capability.Retired,
                        ParseJson(capability.ConstraintsJson),
                        ParseGenericDefinition(capability.GenericDefinitionJson),
                        capability.Attributes
                            .OrderBy(a => a.Key, StringComparer.Ordinal)
                            .Select(attribute => ToDto(attribute, values.GetValueOrDefault(attribute.Id)))
                            .ToList()))
                    .ToList()))
            .ToList();

    private static AttributeDto ToDto(DeviceAttribute attribute, AttributeStateDto? state) =>
        state is null
            ? new AttributeDto(attribute.Id, attribute.Key, StateValue.Null, null, null, Restored: false)
            : new AttributeDto(attribute.Id, attribute.Key, state.Value, state.ObservedAtUtc, state.LastChangedAtUtc, state.Restored);

    private static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static CapabilityDto? ParseGenericDefinition(string? json) =>
        string.IsNullOrWhiteSpace(json) || CapabilityJson.Deserialize<CapabilityDefinition>(json) is not { } definition
            ? null
            : CapabilityMapping.ToDto(definition);

    private static Dictionary<string, string> ParseConnections(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

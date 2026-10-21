using System.Collections.Concurrent;
using ControlHome.Application.Devices;
using ControlHome.Application.Runtime;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Infrastructure.Devices;

/// <summary>How a connector addresses an attribute: its own device and endpoint keys plus capability and attribute keys.</summary>
public readonly record struct AttributeAddress(Guid IntegrationId, string Device, string Endpoint, string Capability, string Attribute);

/// <summary>A known attribute with everything the state pipeline needs about it.</summary>
/// <param name="Definition">The attribute's definition (catalogue or generic), or <c>null</c> when the capability is unknown.</param>
public sealed record AttributeInfo(
    int AttributeId,
    AttributeAddress Address,
    Guid DeviceId,
    Guid EndpointId,
    int EndpointCapabilityId,
    AttributeDefinition? Definition);

/// <summary>A known endpoint capability (bound by its connector, possibly retired) with its definition.</summary>
/// <param name="Definition">The catalogue's definition, or the connector-supplied one for <c>generic</c>; <c>null</c> when unknown.</param>
public sealed record CapabilityEntry(Guid DeviceId, Guid EndpointId, int EndpointCapabilityId, CapabilityDefinition? Definition);

/// <summary>A known device and its live availability.</summary>
public sealed class DeviceEntry(Guid deviceId, Guid integrationId, string externalId, string? deviceClass, Availability availability)
{
    public Guid DeviceId { get; } = deviceId;

    public Guid IntegrationId { get; } = integrationId;

    public string ExternalId { get; } = externalId;

    public string? DeviceClass { get; set; } = deviceClass;

    /// <summary>The last availability the pipeline saw (only the processing loop changes it).</summary>
    public Availability Availability { get; set; } = availability;

    /// <summary>When the device last reported anything (live; the database copy is written at most every minute).</summary>
    public DateTimeOffset? LastSeenAtUtc { get; set; }

    /// <summary>When <c>LastSeenAtUtc</c> was last queued for the database.</summary>
    public DateTimeOffset? LastSeenQueuedAtUtc { get; set; }
}

/// <summary>
/// In-memory index of the device inventory for the state pipeline: connector addresses → <c>AttributeId</c>, attribute
/// ids → their device/endpoint/capability, devices by external id. Loaded from the database when this process becomes the
/// active runtime and kept current by the inventory service. Thread-safe.
/// </summary>
public sealed class DeviceRegistry
{
    private readonly ConcurrentDictionary<AttributeAddress, AttributeInfo> _byAddress = new();
    private readonly ConcurrentDictionary<int, AttributeInfo> _byId = new();
    private readonly ConcurrentDictionary<(Guid IntegrationId, string ExternalId), DeviceEntry> _devices = new();
    private readonly ConcurrentDictionary<Guid, DeviceEntry> _devicesById = new();
    private readonly ConcurrentDictionary<(Guid IntegrationId, string Device, string Endpoint, string Capability), CapabilityEntry> _capabilities = new();

    public int AttributeCount => _byId.Count;

    public int DeviceCount => _devicesById.Count;

    /// <summary>Replaces the registry's content with every device in the database.</summary>
    public async Task LoadAsync(IQueryable<Device> devices, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var all = await devices
            .AsNoTracking()
            .Include(d => d.Endpoints)
            .ThenInclude(e => e.Capabilities)
            .ThenInclude(c => c.Attributes)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        Clear();
        foreach (var device in all)
        {
            Register(device);
        }
    }

    /// <summary>Adds or replaces a device with its whole graph (endpoints, capabilities and attributes must be loaded).</summary>
    public void Register(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var key = (device.IntegrationId, device.ExternalId);
        var entry = _devices.AddOrUpdate(
            key,
            _ => new DeviceEntry(device.Id, device.IntegrationId, device.ExternalId, device.DeviceClass, device.Availability)
            {
                LastSeenAtUtc = device.LastSeenAtUtc,
            },
            (_, existing) =>
            {
                existing.DeviceClass = device.DeviceClass;
                return existing;
            });
        _devicesById[device.Id] = entry;

        foreach (var stale in _byId.Values.Where(a => a.DeviceId == device.Id).ToList())
        {
            _byId.TryRemove(stale.AttributeId, out _);
            _byAddress.TryRemove(stale.Address, out _);
        }

        foreach (var stale in _capabilities.Where(c => c.Value.DeviceId == device.Id).ToList())
        {
            _capabilities.TryRemove(stale.Key, out _);
        }

        foreach (var endpoint in device.Endpoints)
        {
            foreach (var capability in endpoint.Capabilities)
            {
                var definition = DefinitionOf(capability);
                _capabilities[(device.IntegrationId, device.ExternalId, endpoint.Key, capability.CapabilityId)] =
                    new CapabilityEntry(device.Id, endpoint.Id, capability.Id, definition);
                foreach (var attribute in capability.Attributes)
                {
                    Add(new AttributeInfo(
                        attribute.Id,
                        new AttributeAddress(device.IntegrationId, device.ExternalId, endpoint.Key, capability.CapabilityId, attribute.Key),
                        device.Id,
                        endpoint.Id,
                        capability.Id,
                        definition?.FindAttribute(attribute.Key)));
                }
            }
        }
    }

    public void Add(AttributeInfo attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        _byId[attribute.AttributeId] = attribute;
        _byAddress[attribute.Address] = attribute;
    }

    public bool TryResolve(AttributeAddress address, out AttributeInfo attribute) =>
        _byAddress.TryGetValue(address, out attribute!);

    public bool TryGet(int attributeId, out AttributeInfo attribute) => _byId.TryGetValue(attributeId, out attribute!);

    /// <summary>The endpoint capability <paramref name="address"/> belongs to (its attribute key is ignored), or <c>null</c>.</summary>
    public CapabilityEntry? FindCapability(AttributeAddress address) =>
        _capabilities.GetValueOrDefault((address.IntegrationId, address.Device, address.Endpoint, address.Capability));

    public DeviceEntry? FindDevice(Guid integrationId, string externalId) => _devices.GetValueOrDefault((integrationId, externalId));

    public DeviceEntry? FindDevice(Guid deviceId) => _devicesById.GetValueOrDefault(deviceId);

    public IReadOnlyList<DeviceEntry> DevicesOf(Guid integrationId) =>
        _devicesById.Values.Where(d => d.IntegrationId == integrationId).ToList();

    /// <summary>Forgets an integration's devices; returns their attribute ids.</summary>
    public IReadOnlyList<int> RemoveIntegration(Guid integrationId)
    {
        var removed = new List<int>();
        foreach (var attribute in _byId.Values.Where(a => a.Address.IntegrationId == integrationId).ToList())
        {
            _byId.TryRemove(attribute.AttributeId, out _);
            _byAddress.TryRemove(attribute.Address, out _);
            removed.Add(attribute.AttributeId);
        }

        foreach (var device in _devicesById.Values.Where(d => d.IntegrationId == integrationId).ToList())
        {
            _devicesById.TryRemove(device.DeviceId, out _);
            _devices.TryRemove((device.IntegrationId, device.ExternalId), out _);
        }

        foreach (var capability in _capabilities.Keys.Where(k => k.IntegrationId == integrationId).ToList())
        {
            _capabilities.TryRemove(capability, out _);
        }

        return removed;
    }

    public void Clear()
    {
        _byAddress.Clear();
        _byId.Clear();
        _devices.Clear();
        _devicesById.Clear();
        _capabilities.Clear();
    }

    /// <summary>The capability's definition: the catalogue's, or the connector-supplied one for <c>generic</c>.</summary>
    public static CapabilityDefinition? DefinitionOf(EndpointCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        return EndpointCapability.ResolveDefinition(capability.CapabilityId, capability.GenericDefinitionJson);
    }
}

/// <summary>
/// <see cref="IDeviceStatusReader"/> of the runtime: while this process is the active runtime, availability and last-seen
/// come from the <see cref="DeviceRegistry"/> (what the pipeline and the hub see right now), not from the write-behind's
/// database copy.
/// </summary>
public sealed class RuntimeDeviceStatusReader(DeviceRegistry registry, IRuntimeStatus status) : IDeviceStatusReader
{
    public IReadOnlyDictionary<Guid, DeviceLiveStatus> Get(IReadOnlyCollection<Guid> deviceIds)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        var result = new Dictionary<Guid, DeviceLiveStatus>();
        if (status.Mode != RuntimeMode.Active)
        {
            return result;
        }

        foreach (var id in deviceIds)
        {
            if (registry.FindDevice(id) is { } device)
            {
                result[id] = new DeviceLiveStatus(device.Availability, device.LastSeenAtUtc);
            }
        }

        return result;
    }
}

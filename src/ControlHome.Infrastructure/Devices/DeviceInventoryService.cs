using System.Text.Json;
using ControlHome.Application.Abstractions;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ControlHome.Infrastructure.Devices;

/// <summary>The inventory side of the state pipeline (connector upserts and removals, attributes created on first report).</summary>
public interface IDeviceInventory
{
    /// <summary>Adds or merges a device reported by a connector (see <see cref="DeviceInventoryService"/>).</summary>
    /// <exception cref="ArgumentException">The discovered device is malformed (a connector bug).</exception>
    Task UpsertAsync(Guid integrationId, DiscoveredDevice device, CancellationToken cancellationToken);

    /// <summary>
    /// The connector no longer has the device: it becomes orphaned (never deleted) and Unknown in the database. The caller
    /// reports the Unknown availability through the state pipeline, so it is ordered with the device's other reports.
    /// </summary>
    Task RemoveAsync(Guid integrationId, string externalDeviceId, CancellationToken cancellationToken);

    /// <summary>
    /// The attribute at <paramref name="address"/>, created when its device, endpoint and capability exist and the
    /// capability's definition has the key; <c>null</c> otherwise. Called on the state pipeline's loop: answers from memory
    /// whenever it can and throws (<see cref="TimeoutException"/>, database errors) rather than wait long.
    /// </summary>
    Task<AttributeInfo?> EnsureAttributeAsync(AttributeAddress address, CancellationToken cancellationToken);
}

/// <summary>
/// Merges connector inventories into the device model (milestone 1.2 rules; 1.3 adds duplicate detection and replace):
/// devices match on (integration, external id); vendor fields (class, manufacturer, model, firmware, connections, parent)
/// update; user fields (name, room, icon; endpoint name) are only set when the device is first imported (room from the
/// suggested room name); new endpoints, capabilities and attributes are added; capabilities no longer reported are retired
/// (history kept). Every change updates the <see cref="DeviceRegistry"/> and pushes hub <c>DevicesChanged</c>. Operations
/// run one at a time.
/// </summary>
public sealed partial class DeviceInventoryService(
    IServiceScopeFactory scopes,
    DeviceRegistry registry,
    RealtimeBatcher realtime,
    RuntimeClock clock,
    ILogger<DeviceInventoryService> logger) : IDeviceInventory
{
    /// <summary>How long <see cref="EnsureAttributeAsync"/> waits for an upsert or removal in progress before giving up.</summary>
    internal static readonly TimeSpan GateWait = TimeSpan.FromMilliseconds(50);

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task UpsertAsync(Guid integrationId, DiscoveredDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        var bindings = Validate(device);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IControlHomeDb>();
            var existing = await db.Devices
                .Include(d => d.Endpoints)
                .ThenInclude(e => e.Capabilities)
                .ThenInclude(c => c.Attributes)
                .AsSplitQuery()
                .SingleOrDefaultAsync(d => d.IntegrationId == integrationId && d.ExternalId == device.ExternalId, cancellationToken)
                .ConfigureAwait(false);

            var parentId = device.ParentExternalId is { } parent
                ? await db.Devices
                    .Where(d => d.IntegrationId == integrationId && d.ExternalId == parent)
                    .Select(d => (Guid?)d.Id)
                    .SingleOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false)
                : null;

            Device target;
            if (existing is null)
            {
                target = await CreateAsync(db, integrationId, device, parentId, cancellationToken).ConfigureAwait(false);
                db.Devices.Add(target);
            }
            else
            {
                target = existing;
                UpdateVendorFields(target, device, parentId);
                target.IsOrphaned = false;
            }

            MergeEndpoints(target, device, bindings);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            registry.Register(target);
            if (existing is null)
            {
                LogImported(integrationId, device.ExternalId, target.Id);
            }
        }
        finally
        {
            _gate.Release();
        }

        realtime.DevicesChanged();
    }

    public async Task RemoveAsync(Guid integrationId, string externalDeviceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(externalDeviceId);

        int changed;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IControlHomeDb>();
            changed = await db.Devices
                .Where(d => d.IntegrationId == integrationId && d.ExternalId == externalDeviceId && !d.IsOrphaned)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(d => d.IsOrphaned, true).SetProperty(d => d.Availability, Availability.Unknown),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (changed > 0)
        {
            // The registry's availability (and the hub push) follow through the state pipeline (see the interface).
            LogOrphaned(integrationId, externalDeviceId);
            realtime.DevicesChanged();
        }
    }

    public async Task<AttributeInfo?> EnsureAttributeAsync(AttributeAddress address, CancellationToken cancellationToken)
    {
        // From memory first: an upsert creates a row for every attribute of each capability it binds and registers them
        // all, so an unbound endpoint/capability or a key its definition lacks is unknown without asking the database.
        if (registry.FindDevice(address.IntegrationId, address.Device) is not { } device
            || registry.FindCapability(address) is not { } bound
            || bound.Definition?.FindAttribute(address.Attribute) is null)
        {
            return null;
        }

        // Only a key the definition has but the database lacks (the catalogue gained it since the last upsert) gets here.
        // Never queue behind an upsert or removal holding the gate across its own database work.
        if (!await _gate.WaitAsync(GateWait, cancellationToken).ConfigureAwait(false))
        {
            throw new TimeoutException("The device inventory is busy.");
        }

        try
        {
            if (registry.TryResolve(address, out var known))
            {
                return known;
            }

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IControlHomeDb>();
            var capability = await db.EndpointCapabilities
                .Include(c => c.Attributes)
                .Where(c => c.Id == bound.EndpointCapabilityId)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (capability is null || DeviceRegistry.DefinitionOf(capability)?.FindAttribute(address.Attribute) is not { } definition)
            {
                return null;
            }

            var attribute = capability.Attributes.SingleOrDefault(a => a.Key == address.Attribute);
            if (attribute is null)
            {
                attribute = new DeviceAttribute { Key = address.Attribute };
                capability.Attributes.Add(attribute);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            var info = new AttributeInfo(attribute.Id, address, device.DeviceId, capability.EndpointId, capability.Id, definition);
            registry.Add(info);
            return info;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Checks the discovered device and resolves every capability's definition.</summary>
    private static Dictionary<(string Endpoint, string Capability), (CapabilityBinding Binding, CapabilityDefinition Definition)> Validate(
        DiscoveredDevice device)
    {
        if (string.IsNullOrWhiteSpace(device.ExternalId) || device.ExternalId.Length > DeviceLimits.ExternalIdMaxLength)
        {
            throw new ArgumentException("A discovered device needs an external id of at most 256 characters.", nameof(device));
        }

        if (device.Endpoints is not { Count: > 0 })
        {
            throw new ArgumentException($"Device '{device.ExternalId}' has no endpoints.", nameof(device));
        }

        var result = new Dictionary<(string, string), (CapabilityBinding, CapabilityDefinition)>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in device.Endpoints)
        {
            if (string.IsNullOrWhiteSpace(endpoint.Key) || endpoint.Key.Length > DeviceLimits.EndpointKeyMaxLength || !keys.Add(endpoint.Key))
            {
                throw new ArgumentException($"Device '{device.ExternalId}' has a missing, too long or repeated endpoint key.", nameof(device));
            }

            foreach (var binding in endpoint.Capabilities)
            {
                var definition = DefinitionOf(binding)
                    ?? throw new ArgumentException(
                        $"Device '{device.ExternalId}' endpoint '{endpoint.Key}' has an unknown capability '{binding.CapabilityId}'.",
                        nameof(device));
                if (!result.TryAdd((endpoint.Key, binding.CapabilityId), (binding, definition)))
                {
                    throw new ArgumentException(
                        $"Device '{device.ExternalId}' endpoint '{endpoint.Key}' lists capability '{binding.CapabilityId}' twice.",
                        nameof(device));
                }
            }
        }

        return result;
    }

    private static CapabilityDefinition? DefinitionOf(CapabilityBinding binding) =>
        string.Equals(binding.CapabilityId, CapabilityIds.Generic, StringComparison.Ordinal)
            ? binding.GenericDefinition is { Id: CapabilityIds.Generic } generic ? generic : null
            : binding.GenericDefinition is null ? CapabilityCatalog.Find(binding.CapabilityId) : null;

    private async Task<Device> CreateAsync(
        IControlHomeDb db,
        Guid integrationId,
        DiscoveredDevice discovered,
        Guid? parentId,
        CancellationToken cancellationToken)
    {
        int? roomId = null;
        if (!string.IsNullOrWhiteSpace(discovered.SuggestedRoom))
        {
            var suggested = discovered.SuggestedRoom.Trim();
            var rooms = await db.Rooms.AsNoTracking().Select(r => new { r.Id, r.Name }).ToListAsync(cancellationToken).ConfigureAwait(false);
            roomId = rooms
                .Where(r => string.Equals(r.Name.Trim(), suggested, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.Id)
                .Select(r => (int?)r.Id)
                .FirstOrDefault();
        }

        var device = new Device
        {
            Id = Guid.CreateVersion7(),
            IntegrationId = integrationId,
            ExternalId = discovered.ExternalId,
            Name = Truncate(string.IsNullOrWhiteSpace(discovered.Name) ? discovered.ExternalId : discovered.Name.Trim(), DeviceLimits.NameMaxLength)!,
            Availability = Availability.Unknown,
            RoomId = roomId,
            CreatedAtUtc = clock.Time.GetUtcNow(),
        };
        UpdateVendorFields(device, discovered, parentId);
        return device;
    }

    private static void UpdateVendorFields(Device device, DiscoveredDevice discovered, Guid? parentId)
    {
        device.DeviceClass = Truncate(discovered.DeviceClass, DeviceLimits.DeviceClassMaxLength);
        device.Manufacturer = Truncate(discovered.Manufacturer, DeviceLimits.ManufacturerMaxLength);
        device.Model = Truncate(discovered.Model, DeviceLimits.ModelMaxLength);
        device.Firmware = Truncate(discovered.Firmware, DeviceLimits.FirmwareMaxLength);
        device.ConnectionsJson = JsonSerializer.Serialize(discovered.Connections ?? new Dictionary<string, string>());
        device.ParentDeviceId = parentId == device.Id ? null : parentId;
    }

    private static void MergeEndpoints(
        Device device,
        DiscoveredDevice discovered,
        Dictionary<(string Endpoint, string Capability), (CapabilityBinding Binding, CapabilityDefinition Definition)> bindings)
    {
        var order = 0;
        foreach (var discoveredEndpoint in discovered.Endpoints)
        {
            var endpoint = device.Endpoints.SingleOrDefault(e => e.Key == discoveredEndpoint.Key);
            if (endpoint is null)
            {
                endpoint = new Endpoint
                {
                    Id = Guid.CreateVersion7(),
                    Key = discoveredEndpoint.Key,
                    Name = Truncate(discoveredEndpoint.Name?.Trim(), DeviceLimits.NameMaxLength),
                    SortOrder = order,
                };
                device.Endpoints.Add(endpoint);
            }

            order++;
            foreach (var binding in discoveredEndpoint.Capabilities)
            {
                var (_, definition) = bindings[(discoveredEndpoint.Key, binding.CapabilityId)];
                var capability = endpoint.Capabilities.SingleOrDefault(c => c.CapabilityId == binding.CapabilityId);
                if (capability is null)
                {
                    capability = new EndpointCapability { CapabilityId = binding.CapabilityId };
                    endpoint.Capabilities.Add(capability);
                }

                capability.CapabilityVersion = definition.Version;
                capability.ConstraintsJson = binding.Constraints is { ValueKind: JsonValueKind.Object } constraints ? constraints.GetRawText() : null;
                capability.GenericDefinitionJson = binding.GenericDefinition is null ? null : CapabilityJson.Serialize(binding.GenericDefinition);
                capability.Retired = false;
                foreach (var attribute in definition.Attributes)
                {
                    if (!capability.Attributes.Any(a => a.Key == attribute.Key))
                    {
                        capability.Attributes.Add(new DeviceAttribute { Key = attribute.Key });
                    }
                }
            }
        }

        // Capabilities (and endpoints) the connector no longer reports are retired, never deleted: history stays.
        foreach (var endpoint in device.Endpoints)
        {
            foreach (var capability in endpoint.Capabilities)
            {
                if (!bindings.ContainsKey((endpoint.Key, capability.CapabilityId)))
                {
                    capability.Retired = true;
                }
            }
        }
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null ? null : value.Length <= maxLength ? value : value[..maxLength];

    [LoggerMessage(Level = LogLevel.Information, Message = "Integration {IntegrationId} imported device {ExternalId} as {DeviceId}")]
    private partial void LogImported(Guid integrationId, string externalId, Guid deviceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Integration {IntegrationId} no longer reports device {ExternalId}: orphaned")]
    private partial void LogOrphaned(Guid integrationId, string externalId);
}

using System.Data;
using System.Globalization;
using System.Text.Json;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Common;
using ControlHome.Application.Devices;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using ControlHome.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
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
    /// A full inventory of the integration: every listed device is added or merged (parents before their children), every
    /// other device of the integration becomes orphaned (never deleted). Returns the external ids of the devices it
    /// orphaned; as with <see cref="RemoveAsync"/>, the caller reports their Unknown availability through the pipeline.
    /// </summary>
    /// <exception cref="ArgumentException">A listed device is malformed, or two share an external id (a connector bug).</exception>
    Task<IReadOnlyList<string>> SyncAsync(Guid integrationId, IReadOnlyList<DiscoveredDevice> devices, CancellationToken cancellationToken);

    /// <summary>
    /// The attribute at <paramref name="address"/>, created when its device, endpoint and capability exist and the
    /// capability's definition has the key; <c>null</c> otherwise. Called on the state pipeline's loop: answers from memory
    /// whenever it can and throws (<see cref="TimeoutException"/>, database errors) rather than wait long.
    /// </summary>
    Task<AttributeInfo?> EnsureAttributeAsync(AttributeAddress address, CancellationToken cancellationToken);
}

/// <summary>
/// Merges connector inventories into the device model: devices match on (integration, external id); vendor fields (class,
/// manufacturer, model, firmware, connections, parent) update; user fields (name, room, icon; endpoint name) are only set
/// when the device is first imported (room from the suggested room name); new endpoints, capabilities and attributes are
/// added; capabilities no longer reported are retired (history kept); a device missing from a full inventory
/// (<see cref="SyncAsync"/>) or removed by its connector becomes orphaned (never deleted). <see cref="ReplaceAsync"/> moves a
/// device's user settings, placements and history to another one. Every change updates the <see cref="DeviceRegistry"/>
/// and pushes hub <c>DevicesChanged</c>. Operations run one at a time. (Likely duplicates across integrations — the same
/// MAC or serial — are flagged when devices are read, see <c>DeviceIdentity</c>.)
/// </summary>
public sealed partial class DeviceInventoryService(
    IServiceScopeFactory scopes,
    DeviceRegistry registry,
    RealtimeBatcher realtime,
    RuntimeClock clock,
    ILogger<DeviceInventoryService> logger,
    StateCache? cache = null) : IDeviceInventory, IDeviceInventoryAdmin
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

    public async Task<IReadOnlyList<string>> SyncAsync(
        Guid integrationId,
        IReadOnlyList<DiscoveredDevice> devices,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in devices)
        {
            ArgumentNullException.ThrowIfNull(device, nameof(devices));
            Validate(device);
            if (!listed.Add(device.ExternalId))
            {
                throw new ArgumentException($"The inventory lists device '{device.ExternalId}' twice.", nameof(devices));
            }
        }

        foreach (var device in ParentsFirst(devices))
        {
            await UpsertAsync(integrationId, device, cancellationToken).ConfigureAwait(false);
        }

        List<string> orphaned;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IControlHomeDb>();

            // The database compares the external ids, as it does when an upsert looks a device up (its collation, the
            // same as the unique key's): a device reported as "ABC" that was stored as "abc" was just merged, not missing.
            var ids = listed.ToList();
            var missing = await db.Devices
                .Where(d => d.IntegrationId == integrationId && !d.IsOrphaned && !ids.Contains(d.ExternalId))
                .Select(d => new { d.Id, d.ExternalId })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            orphaned = [.. missing.Select(d => d.ExternalId)];
            if (missing.Count > 0)
            {
                var missingIds = missing.Select(d => d.Id).ToList();
                await db.Devices
                    .Where(d => missingIds.Contains(d.Id))
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(d => d.IsOrphaned, true).SetProperty(d => d.Availability, Availability.Unknown),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (orphaned.Count > 0)
        {
            foreach (var externalId in orphaned)
            {
                LogOrphaned(integrationId, externalId);
            }

            realtime.DevicesChanged();
        }

        return orphaned;
    }

    public async Task<Result> ReplaceAsync(Guid oldDeviceId, Guid newDeviceId, CancellationToken cancellationToken)
    {
        Result result;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ControlHomeDbContext>();
            var strategy = db.Database.CreateExecutionStrategy();
            result = await strategy.ExecuteAsync(
                    async () =>
                    {
                        db.ChangeTracker.Clear();
                        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                        var replaced = await ReplaceInTransactionAsync(db, oldDeviceId, newDeviceId, cancellationToken).ConfigureAwait(false);
                        if (replaced.IsSuccess)
                        {
                            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        }

                        return replaced;
                    })
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (result.IsSuccess)
        {
            cache?.Remove(registry.RemoveDevice(oldDeviceId));
            LogReplaced(oldDeviceId, newDeviceId);
            realtime.DevicesChanged();
        }

        return result;
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

    /// <summary>One replace, inside the caller's transaction (committed by the caller when this succeeds).</summary>
    private static async Task<Result> ReplaceInTransactionAsync(
        ControlHomeDbContext db,
        Guid oldDeviceId,
        Guid newDeviceId,
        CancellationToken cancellationToken)
    {
        var oldDevice = await LoadGraphAsync(db, oldDeviceId, cancellationToken).ConfigureAwait(false);
        if (oldDevice is null)
        {
            return AppError.NotFound(ErrorCodes.DevicesNotFound, "No such device.");
        }

        if (newDeviceId == oldDeviceId)
        {
            return NewDeviceError(ErrorCodes.DevicesReplaceSame);
        }

        var newDevice = await LoadGraphAsync(db, newDeviceId, cancellationToken).ConfigureAwait(false);
        if (newDevice is null)
        {
            return NewDeviceError(ErrorCodes.DevicesNotFound);
        }

        // Endpoints match by key, or the only endpoint of each; attributes by capability and key.
        var endpoints = new List<(Endpoint Old, Endpoint New)>();
        foreach (var oldEndpoint in oldDevice.Endpoints)
        {
            var match = newDevice.Endpoints.SingleOrDefault(e => e.Key == oldEndpoint.Key)
                ?? (oldDevice.Endpoints.Count == 1 && newDevice.Endpoints.Count == 1 ? newDevice.Endpoints[0] : null);
            if (match is not null)
            {
                endpoints.Add((oldEndpoint, match));
            }
        }

        var attributes = new List<(int From, int To)>();
        foreach (var (oldEndpoint, newEndpoint) in endpoints)
        {
            foreach (var oldCapability in oldEndpoint.Capabilities)
            {
                var newCapability = newEndpoint.Capabilities.SingleOrDefault(c => c.CapabilityId == oldCapability.CapabilityId);
                foreach (var oldAttribute in oldCapability.Attributes)
                {
                    if (newCapability?.Attributes.SingleOrDefault(a => a.Key == oldAttribute.Key) is { } newAttribute)
                    {
                        attributes.Add((oldAttribute.Id, newAttribute.Id));
                    }
                }
            }
        }

        // User fields.
        newDevice.Name = oldDevice.Name;
        newDevice.RoomId = oldDevice.RoomId;
        newDevice.Icon = oldDevice.Icon;
        foreach (var (oldEndpoint, newEndpoint) in endpoints)
        {
            newEndpoint.Name = oldEndpoint.Name;
            newEndpoint.RoomId = oldEndpoint.RoomId;
            newEndpoint.Icon = oldEndpoint.Icon;
            newEndpoint.Hidden = oldEndpoint.Hidden;
            newEndpoint.Favorite = oldEndpoint.Favorite;
        }

        // Placements: the old device's take the place of the new one's.
        if (await db.Placements.SingleOrDefaultAsync(p => p.DeviceId == oldDeviceId, cancellationToken).ConfigureAwait(false) is { } devicePlacement)
        {
            await db.Placements.Where(p => p.DeviceId == newDeviceId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            devicePlacement.DeviceId = newDeviceId;
        }

        foreach (var (oldEndpoint, newEndpoint) in endpoints)
        {
            if (await db.Placements.SingleOrDefaultAsync(p => p.EndpointId == oldEndpoint.Id, cancellationToken).ConfigureAwait(false) is { } placement)
            {
                await db.Placements.Where(p => p.EndpointId == newEndpoint.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                placement.EndpointId = newEndpoint.Id;
            }
        }

        // History follows its attributes and endpoints (where the new device already has a bucket, its own row wins).
        await db.Database.ExecuteSqlRawAsync(
                MoveHistorySql,
                [
                    new SqlParameter("@attributeMap", SqlDbType.NVarChar, -1)
                    {
                        Value = JsonSerializer.Serialize(attributes.Select(a => new { f = a.From, t = a.To })),
                    },
                    new SqlParameter("@endpointMap", SqlDbType.NVarChar, -1)
                    {
                        Value = JsonSerializer.Serialize(endpoints.Select(e => new { f = e.Old.Id, t = e.New.Id })),
                    },
                ],
                cancellationToken)
            .ConfigureAwait(false);

        // Parent links do not cascade: the old device's children (of its own integration) lose their parent.
        await db.Devices
            .Where(d => d.ParentDeviceId == oldDeviceId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ParentDeviceId, (Guid?)null), cancellationToken)
            .ConfigureAwait(false);
        db.Devices.Remove(oldDevice);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();

        static AppError NewDeviceError(string code) =>
            AppError.FieldErrors(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["newDeviceId"] = [code] });
    }

    private static Task<Device?> LoadGraphAsync(ControlHomeDbContext db, Guid deviceId, CancellationToken cancellationToken) =>
        db.Devices
            .Include(d => d.Endpoints)
            .ThenInclude(e => e.Capabilities)
            .ThenInclude(c => c.Attributes)
            .AsSplitQuery()
            .SingleOrDefaultAsync(d => d.Id == deviceId, cancellationToken);

    /// <summary>
    /// Re-keys history from the old attributes and endpoints to the new ones (maps as JSON <c>[{f, t}]</c>). Statistics and
    /// energy rows the new device already has for the same bucket are kept and the old ones dropped; the energy source
    /// selection moves unless the new endpoint has one.
    /// </summary>
    private const string MoveHistorySql = """
        DECLARE @attributes TABLE (FromId int PRIMARY KEY, ToId int NOT NULL);
        INSERT INTO @attributes (FromId, ToId) SELECT f, t FROM OPENJSON(@attributeMap) WITH (f int '$.f', t int '$.t');
        DECLARE @endpoints TABLE (FromId uniqueidentifier PRIMARY KEY, ToId uniqueidentifier NOT NULL);
        INSERT INTO @endpoints (FromId, ToId) SELECT f, t FROM OPENJSON(@endpointMap) WITH (f uniqueidentifier '$.f', t uniqueidentifier '$.t');

        UPDATE s SET AttributeId = m.ToId FROM [ch].[StateLog] AS s JOIN @attributes AS m ON s.AttributeId = m.FromId;
        UPDATE e SET AttributeId = m.ToId FROM [ch].[DeviceEvent] AS e JOIN @attributes AS m ON e.AttributeId = m.FromId;

        UPDATE s SET AttributeId = m.ToId FROM [ch].[StatShortTerm] AS s JOIN @attributes AS m ON s.AttributeId = m.FromId
        WHERE NOT EXISTS (SELECT 1 FROM [ch].[StatShortTerm] AS x WHERE x.AttributeId = m.ToId AND x.BucketStartUtc = s.BucketStartUtc);
        DELETE s FROM [ch].[StatShortTerm] AS s JOIN @attributes AS m ON s.AttributeId = m.FromId;

        UPDATE s SET AttributeId = m.ToId FROM [ch].[StatHourly] AS s JOIN @attributes AS m ON s.AttributeId = m.FromId
        WHERE NOT EXISTS (SELECT 1 FROM [ch].[StatHourly] AS x WHERE x.AttributeId = m.ToId AND x.BucketStartUtc = s.BucketStartUtc);
        DELETE s FROM [ch].[StatHourly] AS s JOIN @attributes AS m ON s.AttributeId = m.FromId;

        UPDATE h SET EndpointId = m.ToId FROM [ch].[EnergyHourly] AS h JOIN @endpoints AS m ON h.EndpointId = m.FromId
        WHERE NOT EXISTS (SELECT 1 FROM [ch].[EnergyHourly] AS x WHERE x.EndpointId = m.ToId AND x.HourStartUtc = h.HourStartUtc);
        DELETE h FROM [ch].[EnergyHourly] AS h JOIN @endpoints AS m ON h.EndpointId = m.FromId;

        UPDATE s SET EndpointId = m.ToId, AttributeId = a.ToId
        FROM [ch].[EnergySourceSelection] AS s
        JOIN @endpoints AS m ON s.EndpointId = m.FromId
        LEFT JOIN @attributes AS a ON s.AttributeId = a.FromId
        WHERE NOT EXISTS (SELECT 1 FROM [ch].[EnergySourceSelection] AS x WHERE x.EndpointId = m.ToId);
        DELETE s FROM [ch].[EnergySourceSelection] AS s JOIN @endpoints AS m ON s.EndpointId = m.FromId;
        """;

    /// <summary>
    /// The devices with every parent listed before its children (a child's parent link is resolved when it is merged), in
    /// the given order otherwise. Parents that are not listed, and cycles, do not hold anything back.
    /// </summary>
    private static List<DiscoveredDevice> ParentsFirst(IReadOnlyList<DiscoveredDevice> devices)
    {
        var byId = devices.ToDictionary(d => d.ExternalId, StringComparer.Ordinal);
        var ordered = new List<DiscoveredDevice>(devices.Count);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in devices)
        {
            Add(device, []);
        }

        return ordered;

        void Add(DiscoveredDevice device, HashSet<string> path)
        {
            if (placed.Contains(device.ExternalId) || !path.Add(device.ExternalId))
            {
                return;
            }

            if (device.ParentExternalId is { } parentId && byId.TryGetValue(parentId, out var parent))
            {
                Add(parent, path);
            }

            if (placed.Add(device.ExternalId))
            {
                ordered.Add(device);
            }
        }
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
            // "Escritorio" from a vendor app is the home's "Escritório": case and accents do not count.
            var suggested = discovered.SuggestedRoom.Trim();
            var rooms = await db.Rooms.AsNoTracking().Select(r => new { r.Id, r.Name }).ToListAsync(cancellationToken).ConfigureAwait(false);
            roomId = rooms
                .Where(r => string.Compare(
                    r.Name.Trim(),
                    suggested,
                    CultureInfo.InvariantCulture,
                    CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) == 0)
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Device {OldDeviceId} was replaced by {NewDeviceId} (settings, placements and history moved)")]
    private partial void LogReplaced(Guid oldDeviceId, Guid newDeviceId);
}

using ControlHome.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Application.Devices;

/// <summary>
/// Where current attribute values come from. The default (<see cref="DbAttributeStateReader"/>) reads the persisted
/// <c>AttributeState</c> rows; the device runtime (Infrastructure's <c>AddDeviceRuntime</c>) replaces it with a reader
/// that serves its in-memory state cache while this process is the active runtime.
/// </summary>
public interface IAttributeStateReader
{
    /// <summary>The current values of the given attributes (attributes without a value are left out).</summary>
    Task<IReadOnlyDictionary<int, AttributeStateDto>> GetAsync(IReadOnlyCollection<int> attributeIds, CancellationToken cancellationToken);

    /// <summary>Every current value (client resync after a reconnect).</summary>
    Task<IReadOnlyList<AttributeStateDto>> GetAllAsync(CancellationToken cancellationToken);
}

/// <summary>A device's live availability and last-seen time.</summary>
public sealed record DeviceLiveStatus(Domain.Devices.Availability Availability, DateTimeOffset? LastSeenAtUtc);

/// <summary>
/// Where live device availability comes from. The database copy is written behind (every few seconds, and not at all while
/// the database is down), so while this process is the active runtime the device runtime's own view is served instead
/// (Infrastructure replaces <see cref="NoLiveDeviceStatus"/>); devices it does not return use the database values.
/// </summary>
public interface IDeviceStatusReader
{
    IReadOnlyDictionary<Guid, DeviceLiveStatus> Get(IReadOnlyCollection<Guid> deviceIds);
}

/// <summary>No live view (no device runtime, standby): the database values are used.</summary>
public sealed class NoLiveDeviceStatus : IDeviceStatusReader
{
    public static NoLiveDeviceStatus Instance { get; } = new();

    public IReadOnlyDictionary<Guid, DeviceLiveStatus> Get(IReadOnlyCollection<Guid> deviceIds) => new Dictionary<Guid, DeviceLiveStatus>();
}

/// <summary>Reads current values from the database; all of them are <see cref="AttributeStateDto.Restored"/>.</summary>
public sealed class DbAttributeStateReader(IControlHomeDb db) : IAttributeStateReader
{
    public async Task<IReadOnlyDictionary<int, AttributeStateDto>> GetAsync(
        IReadOnlyCollection<int> attributeIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attributeIds);
        if (attributeIds.Count == 0)
        {
            return new Dictionary<int, AttributeStateDto>();
        }

        var ids = attributeIds.Distinct().ToList();
        var rows = await db.AttributeStates.AsNoTracking().Where(s => ids.Contains(s.AttributeId)).ToListAsync(cancellationToken);
        return rows.ToDictionary(s => s.AttributeId, ToDto);
    }

    public async Task<IReadOnlyList<AttributeStateDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        var rows = await db.AttributeStates.AsNoTracking().OrderBy(s => s.AttributeId).ToListAsync(cancellationToken);
        return rows.Select(ToDto).ToList();
    }

    private static AttributeStateDto ToDto(Domain.Devices.AttributeState state) =>
        new(state.AttributeId, state.GetValue(), state.ObservedAtUtc, state.LastChangedAtUtc, Restored: true);
}

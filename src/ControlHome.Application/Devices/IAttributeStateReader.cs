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

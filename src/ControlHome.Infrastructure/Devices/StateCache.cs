using System.Collections.Concurrent;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Devices;
using ControlHome.Application.Runtime;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Infrastructure.Devices;

/// <summary>The cached current value of one attribute.</summary>
/// <param name="Restored">Loaded from the database and not yet confirmed by the running connector.</param>
public sealed record CachedState(StateValue Value, DateTimeOffset ObservedAtUtc, DateTimeOffset LastChangedAtUtc, bool Restored);

/// <summary>What <see cref="StateCache.Apply"/> did with a report.</summary>
public enum StateApplyOutcome
{
    /// <summary>A new value (or a momentary event): publish, persist, notify.</summary>
    Changed,

    /// <summary>The connector confirmed a restored value: publish and persist (it is live now).</summary>
    Confirmed,

    /// <summary>The same value again (a duplicate or a newer observation of it): nothing to publish or persist.</summary>
    Unchanged,

    /// <summary>Older than the cached observation: dropped.</summary>
    OutOfOrder,
}

/// <summary>The outcome of <see cref="StateCache.Apply"/>, with the entry before and after.</summary>
public readonly record struct StateApplyResult(StateApplyOutcome Outcome, CachedState? Previous, CachedState Current);

/// <summary>
/// The live current value of every attribute (the source of <c>GET /api/state</c> and the device DTOs while this process is
/// the active runtime). Hydrated from <c>AttributeState</c> rows (flagged <see cref="CachedState.Restored"/>) when the runtime
/// starts; afterwards only the state processor's single loop writes to it. Reads are thread-safe.
/// </summary>
public sealed class StateCache
{
    private readonly ConcurrentDictionary<int, CachedState> _states = new();
    private volatile bool _hydrated;

    /// <summary>Whether the cache holds the database's values (else readers fall back to the database).</summary>
    public bool IsHydrated => _hydrated;

    public int Count => _states.Count;

    /// <summary>Replaces the content with the persisted values, all flagged restored.</summary>
    public async Task HydrateAsync(IQueryable<AttributeState> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Load(await rows.AsNoTracking().ToListAsync(cancellationToken));
    }

    /// <summary>Replaces the content with <paramref name="rows"/>, all flagged restored.</summary>
    public void Load(IEnumerable<AttributeState> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _states.Clear();
        foreach (var row in rows)
        {
            _states[row.AttributeId] = new CachedState(row.GetValue(), row.ObservedAtUtc, row.LastChangedAtUtc, Restored: true);
        }

        _hydrated = true;
    }

    /// <summary>
    /// Applies a report: older than the cached observation → <see cref="StateApplyOutcome.OutOfOrder"/>; the same value →
    /// <see cref="StateApplyOutcome.Unchanged"/> (a newer observation time is remembered) unless the cached value was
    /// restored (<see cref="StateApplyOutcome.Confirmed"/>); otherwise <see cref="StateApplyOutcome.Changed"/>. Momentary
    /// events always count as changed. Single writer: call from the state processor's loop only.
    /// </summary>
    public StateApplyResult Apply(int attributeId, StateValue value, DateTimeOffset observedAtUtc, bool isEvent = false)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!_states.TryGetValue(attributeId, out var previous))
        {
            var created = new CachedState(value, observedAtUtc, observedAtUtc, Restored: false);
            _states[attributeId] = created;
            return new StateApplyResult(StateApplyOutcome.Changed, null, created);
        }

        if (isEvent)
        {
            var fired = new CachedState(value, Max(observedAtUtc, previous.ObservedAtUtc), observedAtUtc, Restored: false);
            _states[attributeId] = fired;
            return new StateApplyResult(StateApplyOutcome.Changed, previous, fired);
        }

        if (observedAtUtc < previous.ObservedAtUtc)
        {
            return new StateApplyResult(StateApplyOutcome.OutOfOrder, previous, previous);
        }

        if (previous.Value.Equals(value))
        {
            if (!previous.Restored && observedAtUtc == previous.ObservedAtUtc)
            {
                return new StateApplyResult(StateApplyOutcome.Unchanged, previous, previous);
            }

            var seen = previous with { ObservedAtUtc = observedAtUtc, Restored = false };
            _states[attributeId] = seen;
            return new StateApplyResult(previous.Restored ? StateApplyOutcome.Confirmed : StateApplyOutcome.Unchanged, previous, seen);
        }

        var changed = new CachedState(value, observedAtUtc, observedAtUtc, Restored: false);
        _states[attributeId] = changed;
        return new StateApplyResult(StateApplyOutcome.Changed, previous, changed);
    }

    public bool TryGet(int attributeId, out CachedState state) => _states.TryGetValue(attributeId, out state!);

    public IReadOnlyList<KeyValuePair<int, CachedState>> Snapshot() => [.. _states];

    public void Remove(IEnumerable<int> attributeIds)
    {
        ArgumentNullException.ThrowIfNull(attributeIds);
        foreach (var id in attributeIds)
        {
            _states.TryRemove(id, out _);
        }
    }

    /// <summary>Empties the cache; it stays hydrated (the database was emptied too — test reset).</summary>
    public void Clear() => _states.Clear();

    /// <summary>Back to not hydrated (the runtime stopped: readers use the database).</summary>
    public void Invalidate()
    {
        _hydrated = false;
        _states.Clear();
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}

/// <summary>
/// <see cref="IAttributeStateReader"/> of the runtime: the live <see cref="StateCache"/> while this process is the active
/// runtime, otherwise (standby, starting) the persisted rows written by the active one.
/// </summary>
public sealed class RuntimeAttributeStateReader(StateCache cache, IRuntimeStatus status, IControlHomeDb db) : IAttributeStateReader
{
    public Task<IReadOnlyDictionary<int, AttributeStateDto>> GetAsync(IReadOnlyCollection<int> attributeIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attributeIds);
        if (!UseCache)
        {
            return new DbAttributeStateReader(db).GetAsync(attributeIds, cancellationToken);
        }

        var result = new Dictionary<int, AttributeStateDto>();
        foreach (var id in attributeIds)
        {
            if (cache.TryGet(id, out var state))
            {
                result[id] = ToDto(id, state);
            }
        }

        return Task.FromResult<IReadOnlyDictionary<int, AttributeStateDto>>(result);
    }

    public Task<IReadOnlyList<AttributeStateDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        if (!UseCache)
        {
            return new DbAttributeStateReader(db).GetAllAsync(cancellationToken);
        }

        IReadOnlyList<AttributeStateDto> all = cache.Snapshot().OrderBy(e => e.Key).Select(e => ToDto(e.Key, e.Value)).ToList();
        return Task.FromResult(all);
    }

    private bool UseCache => status.Mode == RuntimeMode.Active && cache.IsHydrated;

    private static AttributeStateDto ToDto(int attributeId, CachedState state) =>
        new(attributeId, state.Value, state.ObservedAtUtc, state.LastChangedAtUtc, state.Restored);
}

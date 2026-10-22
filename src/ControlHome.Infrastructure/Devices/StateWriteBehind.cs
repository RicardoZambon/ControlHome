using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using ControlHome.Infrastructure.History;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlHome.Infrastructure.Devices;

/// <summary>An attribute's current value waiting to be written (<c>AttributeState</c>).</summary>
public sealed record PendingAttributeState(int AttributeId, StateValue Value, DateTimeOffset ObservedAtUtc, DateTimeOffset LastChangedAtUtc);

/// <summary>
/// A history row waiting to be written: a change of a recorded attribute (→ <c>StateLog</c>), or a momentary event
/// (<see cref="IsEvent"/> → <c>DeviceEvent</c>; <see cref="Value"/> is the event name).
/// </summary>
public sealed record PendingHistoryRow(int AttributeId, DateTimeOffset TsUtc, StateValue Value)
{
    public bool IsEvent { get; init; }

    /// <summary>An event's details (a JSON object), if any.</summary>
    public string? EventPayload { get; init; }
}

/// <summary>A device's availability and/or last-seen time waiting to be written (<c>null</c> members are left as they are).</summary>
public sealed record PendingDeviceUpdate(Guid DeviceId, Availability? Availability, DateTimeOffset? LastSeenAtUtc);

/// <summary>One flush of the write-behind buffer.</summary>
public sealed record StateWriteBatch(
    IReadOnlyList<PendingAttributeState> States,
    IReadOnlyList<PendingHistoryRow> History,
    IReadOnlyList<PendingDeviceUpdate> Devices);

/// <summary>Where the write-behind buffer goes (the database; tests substitute a failing or recording store).</summary>
public interface IStateStore
{
    /// <summary>Writes the whole batch or throws (then it is kept and retried with the next flush).</summary>
    Task WriteAsync(StateWriteBatch batch, CancellationToken cancellationToken);
}

/// <summary>
/// Write-behind of the state pipeline: current values (coalesced per attribute — the latest wins, never dropped), history
/// rows (in order; appended to <c>StateLog</c> / <c>DeviceEvent</c>) and device availability/last-seen (coalesced per device)
/// are buffered in memory and written every
/// <see cref="DeviceRuntimeOptions.FlushInterval"/>. While the database is unavailable everything stays buffered; history
/// beyond <see cref="DeviceRuntimeOptions.MaxPendingHistoryRows"/> is dropped oldest-first and the runtime reports
/// <c>HistoryPaused</c> until a flush succeeds again. Control keeps working meanwhile.
/// </summary>
public sealed partial class StateWriteBehind(
    IStateStore store,
    DeviceRuntimeState runtimeState,
    TimeProvider timeProvider,
    IOptions<DeviceRuntimeOptions> options,
    ILogger<StateWriteBehind> logger) : IPendingHistory
{
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private Dictionary<int, PendingAttributeState> _states = [];
    private LinkedList<PendingHistoryRow> _history = new();
    private Dictionary<Guid, PendingDeviceUpdate> _devices = [];
    private DateTimeOffset? _oldestHistoryInFlight;
    private long _droppedHistory;
    private long _enqueuedHistory;
    private CancellationTokenSource? _stop;
    private Task _loop = Task.CompletedTask;

    public int PendingStates
    {
        get
        {
            lock (_lock)
            {
                return _states.Count;
            }
        }
    }

    public int PendingHistory
    {
        get
        {
            lock (_lock)
            {
                return _history.Count;
            }
        }
    }

    public int PendingDevices
    {
        get
        {
            lock (_lock)
            {
                return _devices.Count;
            }
        }
    }

    /// <summary>History rows dropped because the buffer was full (since start).</summary>
    public long DroppedHistory => Interlocked.Read(ref _droppedHistory);

    /// <summary>History rows queued (since start).</summary>
    public long HistoryEnqueued => Interlocked.Read(ref _enqueuedHistory);

    /// <summary>
    /// The oldest observation among the state history rows (not events) still waiting or being written, or <c>null</c>. The
    /// rollups never close a bucket at or after it: its rows are not in <c>StateLog</c> yet.
    /// </summary>
    public DateTimeOffset? OldestPendingHistoryUtc
    {
        get
        {
            lock (_lock)
            {
                var oldest = _oldestHistoryInFlight;
                foreach (var row in _history)
                {
                    if (!row.IsEvent && (oldest is null || row.TsUtc < oldest))
                    {
                        oldest = row.TsUtc;
                    }
                }

                return oldest;
            }
        }
    }

    public void EnqueueState(PendingAttributeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_lock)
        {
            if (!_states.TryGetValue(state.AttributeId, out var existing) || existing.ObservedAtUtc <= state.ObservedAtUtc)
            {
                _states[state.AttributeId] = state;
            }
        }
    }

    public void EnqueueHistory(PendingHistoryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        bool dropped;
        lock (_lock)
        {
            _history.AddLast(row);
            Interlocked.Increment(ref _enqueuedHistory);
            dropped = TrimHistoryLocked();
        }

        if (dropped)
        {
            runtimeState.HistoryPaused = true;
        }
    }

    public void EnqueueDevice(PendingDeviceUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_lock)
        {
            _devices[update.DeviceId] = _devices.TryGetValue(update.DeviceId, out var existing) ? Merge(existing, update) : update;
        }
    }

    /// <summary>Writes everything pending; false (and everything kept) when the store failed.</summary>
    public async Task<bool> FlushAsync(CancellationToken cancellationToken)
    {
        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StateWriteBatch batch;
            lock (_lock)
            {
                if (_states.Count == 0 && _history.Count == 0 && _devices.Count == 0)
                {
                    batch = new StateWriteBatch([], [], []);
                }
                else
                {
                    batch = new StateWriteBatch([.. _states.Values], [.. _history], [.. _devices.Values]);
                    _states = [];
                    _history = new LinkedList<PendingHistoryRow>();
                    _devices = [];
                    _oldestHistoryInFlight = batch.History.Where(h => !h.IsEvent).Select(h => (DateTimeOffset?)h.TsUtc).Min();
                }
            }

            if (batch.States.Count + batch.History.Count + batch.Devices.Count > 0)
            {
                try
                {
                    await store.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    Restore(batch);
                    LogFlushFailed(batch.States.Count, batch.History.Count, ex.GetType().Name);
                    return false;
                }
                catch (OperationCanceledException)
                {
                    Restore(batch);
                    throw;
                }
                finally
                {
                    lock (_lock)
                    {
                        _oldestHistoryInFlight = null;
                    }
                }
            }

            // Everything buffered is written: history (if any was dropped) flows again.
            runtimeState.HistoryPaused = false;
            return true;
        }
        finally
        {
            _flushGate.Release();
        }
    }

    /// <summary>Starts flushing every <see cref="DeviceRuntimeOptions.FlushInterval"/>.</summary>
    public void Start()
    {
        if (_stop is not null)
        {
            return;
        }

        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
    }

    /// <summary>Stops the loop and makes a last flush attempt.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stop is not { } stop)
        {
            return;
        }

        await stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }

        stop.Dispose();
        _stop = null;
        try
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down: what is left is lost (current state is restored from the connectors on the next start).
        }
    }

    /// <summary>
    /// Forgets the device availabilities still waiting (last-seen times are kept). Called before the runtime (re)starts, which
    /// resets every device to Unknown: availability left over from a failed final flush must not override that.
    /// </summary>
    public void DropPendingAvailability()
    {
        lock (_lock)
        {
            foreach (var update in _devices.Values.Where(d => d.Availability is not null).ToList())
            {
                if (update.LastSeenAtUtc is null)
                {
                    _devices.Remove(update.DeviceId);
                }
                else
                {
                    _devices[update.DeviceId] = update with { Availability = null };
                }
            }
        }
    }

    /// <summary>Drops everything pending (test reset).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _states.Clear();
            _history.Clear();
            _devices.Clear();
        }

        runtimeState.HistoryPaused = false;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.Value.FlushInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFlushFailed(0, 0, ex.GetType().Name);
            }
        }
    }

    /// <summary>Puts a failed batch back in front of what arrived meanwhile (newer values win).</summary>
    private void Restore(StateWriteBatch batch)
    {
        bool dropped;
        lock (_lock)
        {
            foreach (var state in batch.States)
            {
                if (!_states.TryGetValue(state.AttributeId, out var newer) || newer.ObservedAtUtc < state.ObservedAtUtc)
                {
                    _states[state.AttributeId] = state;
                }
            }

            for (var i = batch.History.Count - 1; i >= 0; i--)
            {
                _history.AddFirst(batch.History[i]);
            }

            foreach (var device in batch.Devices)
            {
                _devices[device.DeviceId] = _devices.TryGetValue(device.DeviceId, out var newer) ? Merge(device, newer) : device;
            }

            dropped = TrimHistoryLocked();
        }

        if (dropped)
        {
            runtimeState.HistoryPaused = true;
        }
    }

    /// <summary>Drops the oldest history rows beyond the limit; true when any were dropped.</summary>
    private bool TrimHistoryLocked()
    {
        var max = Math.Max(0, options.Value.MaxPendingHistoryRows);
        var dropped = 0;
        while (_history.Count > max)
        {
            _history.RemoveFirst();
            dropped++;
        }

        if (dropped == 0)
        {
            return false;
        }

        Interlocked.Add(ref _droppedHistory, dropped);
        return true;
    }

    /// <summary><paramref name="newer"/>'s members win where set.</summary>
    private static PendingDeviceUpdate Merge(PendingDeviceUpdate older, PendingDeviceUpdate newer) =>
        new(newer.DeviceId, newer.Availability ?? older.Availability, newer.LastSeenAtUtc ?? older.LastSeenAtUtc);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Writing {States} states and {History} history rows to the database failed ({ExceptionType}); kept for the next flush")]
    private partial void LogFlushFailed(int states, int history, string exceptionType);
}

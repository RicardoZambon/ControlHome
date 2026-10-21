namespace ControlHome.Infrastructure.Devices;

/// <summary>
/// A clock that runs in real time but can be moved forward (<see cref="Advance"/>): timers created on it (<c>Task.Delay</c>,
/// timeouts, periodic timers) fire by this clock, so advancing it fires every timer that became due right away. Used as the
/// runtime clock in the <c>Testing</c> environment (<c>POST /api/_test/clock</c>): E2E tests run in real time and jump ahead
/// when they need hours of simulated history.
/// </summary>
public sealed class AdjustableTimeProvider(TimeProvider inner) : TimeProvider
{
    private readonly TimeProvider _inner = inner;
    private readonly Lock _lock = new();
    private readonly HashSet<AdjustableTimer> _timers = [];
    private TimeSpan _offset;

    public AdjustableTimeProvider()
        : this(System)
    {
    }

    /// <summary>How far this clock is ahead of real time.</summary>
    public TimeSpan Offset
    {
        get
        {
            lock (_lock)
            {
                return _offset;
            }
        }
    }

    public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;

    public override long TimestampFrequency => _inner.TimestampFrequency;

    public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow() + Offset;

    public override long GetTimestamp() =>
        _inner.GetTimestamp() + (long)(Offset.Ticks * (double)_inner.TimestampFrequency / TimeSpan.TicksPerSecond);

    /// <summary>Moves the clock forward and fires the timers that became due.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="delta"/> is negative.</exception>
    public void Advance(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
        AdjustableTimer[] timers;
        lock (_lock)
        {
            _offset += delta;
            timers = [.. _timers];
        }

        foreach (var timer in timers)
        {
            timer.Reschedule();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new AdjustableTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private void Track(AdjustableTimer timer, bool active)
    {
        lock (_lock)
        {
            if (active)
            {
                _timers.Add(timer);
            }
            else
            {
                _timers.Remove(timer);
            }
        }
    }

    private sealed class AdjustableTimer : ITimer
    {
        private readonly AdjustableTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private readonly ITimer _real;
        private readonly Lock _lock = new();
        private DateTimeOffset? _dueAt;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        private bool _disposed;

        public AdjustableTimer(AdjustableTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            _real = owner._inner.CreateTimer(static s => ((AdjustableTimer)s!).Fire(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            bool active;
            lock (_lock)
            {
                if (_disposed)
                {
                    return false;
                }

                _period = period;
                _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _owner.GetUtcNow() + dueTime;
                active = _dueAt is not null;
                ScheduleLocked();
            }

            _owner.Track(this, active);
            return true;
        }

        public void Reschedule()
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    ScheduleLocked();
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                _dueAt = null;
            }

            _owner.Track(this, active: false);
            _real.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        private void ScheduleLocked()
        {
            if (_dueAt is not { } dueAt)
            {
                _real.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return;
            }

            var remaining = dueAt - _owner.GetUtcNow();
            _real.Change(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }

        private void Fire()
        {
            bool done;
            lock (_lock)
            {
                if (_disposed || _dueAt is not { } dueAt)
                {
                    return;
                }

                if (_owner.GetUtcNow() < dueAt)
                {
                    // Woken early (rescheduled meanwhile): wait for the rest.
                    ScheduleLocked();
                    return;
                }

                _dueAt = _period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero ? null : dueAt + _period;
                done = _dueAt is null;
                ScheduleLocked();
            }

            if (done)
            {
                _owner.Track(this, active: false);
            }

            _callback(_state);
        }
    }
}

using System.Threading.RateLimiting;

namespace ControlHome.Connectors.SmartThings.Runtime;

/// <summary>
/// "At most N per window" on the connector's clock: a System.Threading.RateLimiting <see cref="RateLimiter"/> that keeps
/// the time of every permit it handed out within the last window. The built-in limiters cannot be used here: they
/// replenish from <see cref="System.Diagnostics.Stopwatch"/> even with manual replenishment, while every delay and
/// timestamp of a connector must follow <c>IConnectorContext.Time</c> (tests drive it with a fake clock). Exact: no burst
/// can exceed the limit within any window. Never queues. Thread-safe.
/// </summary>
internal sealed class ClockedSlidingWindow : RateLimiter
{
    private readonly int _permits;
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Queue<DateTimeOffset> _granted = new();
    private DateTimeOffset? _lastGranted;
    private long _successful;
    private long _failed;

    public ClockedSlidingWindow(int permits, TimeSpan window, TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permits);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        _permits = permits;
        _window = window;
        _time = time;
    }

    /// <summary>Permits free right now.</summary>
    public long Available
    {
        get
        {
            lock (_lock)
            {
                Prune(_time.GetUtcNow());
                return _permits - _granted.Count;
            }
        }
    }

    public override TimeSpan? IdleDuration
    {
        get
        {
            lock (_lock)
            {
                return _granted.Count == 0 ? _lastGranted is { } last ? _time.GetUtcNow() - last : TimeSpan.Zero : null;
            }
        }
    }

    /// <summary>Takes a permit if one is free while keeping <paramref name="reserve"/> spare.</summary>
    public bool TryAcquire(int reserve = 0) => TryAcquire(1, reserve);

    /// <summary>
    /// Takes <paramref name="permits"/> permits at once if they are free while keeping <paramref name="reserve"/> spare,
    /// else none. Atomic: the check and the grant happen under one lock, so concurrent callers (hot polls, commands)
    /// can never eat into the reserve or exceed the limit between them.
    /// </summary>
    public bool TryAcquire(int permits, int reserve)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permits);
        ArgumentOutOfRangeException.ThrowIfNegative(reserve);
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            Prune(now);
            if (_permits - _granted.Count - permits < reserve)
            {
                _failed++;
                return false;
            }

            Grant(permits, now);
            return true;
        }
    }

    public override RateLimiterStatistics? GetStatistics()
    {
        lock (_lock)
        {
            Prune(_time.GetUtcNow());
            return new RateLimiterStatistics
            {
                CurrentAvailablePermits = _permits - _granted.Count,
                CurrentQueuedCount = 0,
                TotalSuccessfulLeases = _successful,
                TotalFailedLeases = _failed,
            };
        }
    }

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(permitCount);
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            Prune(now);
            if (permitCount > _permits - _granted.Count)
            {
                _failed++;
                var retryAfter = _granted.Count > 0 ? _granted.Peek() + _window - now : _window;
                return new Lease(false, retryAfter);
            }

            Grant(permitCount, now);
            return new Lease(true, null);
        }
    }

    /// <summary>Records <paramref name="permitCount"/> granted permits (under the lock).</summary>
    private void Grant(int permitCount, DateTimeOffset now)
    {
        for (var i = 0; i < permitCount; i++)
        {
            _granted.Enqueue(now);
        }

        if (permitCount > 0)
        {
            _lastGranted = now;
        }

        _successful++;
    }

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
        ValueTask.FromResult(AttemptAcquireCore(permitCount));

    private void Prune(DateTimeOffset now)
    {
        while (_granted.Count > 0 && _granted.Peek() <= now - _window)
        {
            _granted.Dequeue();
        }
    }

    private sealed class Lease(bool acquired, TimeSpan? retryAfter) : RateLimitLease
    {
        public override bool IsAcquired { get; } = acquired;

        public override IEnumerable<string> MetadataNames => retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (retryAfter is { } wait && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = wait;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}

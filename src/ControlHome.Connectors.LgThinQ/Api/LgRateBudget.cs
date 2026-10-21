using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;

namespace ControlHome.Connectors.LgThinQ.Api;

/// <summary>The call budget of one personal access token (LG publishes no number; these defaults are conservative).</summary>
/// <param name="Burst">Calls that may go out back to back.</param>
/// <param name="Period">One more call becomes available every period (sustained rate).</param>
/// <param name="QuotaBackoff">How long every call is paused after LG answers 1306 (call limit exceeded).</param>
internal sealed record LgRateOptions(int Burst, TimeSpan Period, TimeSpan QuotaBackoff)
{
    /// <summary>About one call every 10 s sustained, bursts of 5, an hour of silence after a 1306.</summary>
    public static LgRateOptions Default { get; } = new(5, TimeSpan.FromSeconds(10), TimeSpan.FromHours(1));
}

/// <summary>
/// One token's calls: a token bucket shared by everything that calls LG for it (REST, subscriptions, energy, the setup
/// flow), and an hour-long pause once LG says the hourly limit is used up (1306). Every HTTP attempt takes a token,
/// retries included. Calls during the pause fail at once with <see cref="LgQuotaBlockedException"/>.
/// </summary>
internal sealed class LgRateBudget : IDisposable
{
    private readonly TimeProvider _time;
    private readonly LgRateOptions _options;
    private readonly TimeProviderTokenBucket _bucket;
    private readonly Lock _lock = new();
    private readonly Queue<DateTimeOffset> _recent = new();
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;

    public LgRateBudget(TimeProvider time, LgRateOptions options)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        _time = time;
        _options = options;
        _bucket = new TimeProviderTokenBucket(time, options.Burst, options.Period);
    }

    /// <summary>Until when calls are paused after a 1306, or <c>null</c> when they are not.</summary>
    public DateTimeOffset? BlockedUntil
    {
        get
        {
            lock (_lock)
            {
                return _blockedUntil > _time.GetUtcNow() ? _blockedUntil : null;
            }
        }
    }

    /// <summary>Calls made in the last hour (logged when LG answers 1306, to tune the budget).</summary>
    public int CallsLastHour
    {
        get
        {
            lock (_lock)
            {
                Trim(_time.GetUtcNow());
                return _recent.Count;
            }
        }
    }

    /// <summary>Waits for a token for one HTTP attempt.</summary>
    /// <exception cref="LgQuotaBlockedException">Calls are paused after a 1306.</exception>
    public async ValueTask AcquireAsync(CancellationToken cancellationToken)
    {
        ThrowIfBlocked();
        using var lease = await _bucket.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
        if (!lease.IsAcquired)
        {
            throw new InvalidOperationException("The LG ThinQ call budget did not grant a call.");
        }

        ThrowIfBlocked();
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            _recent.Enqueue(now);
            Trim(now);
        }
    }

    /// <summary>LG said the call limit is used up: pause every call for <see cref="LgRateOptions.QuotaBackoff"/> (or longer when LG says so).</summary>
    public DateTimeOffset Block(TimeSpan? retryAfter = null)
    {
        var backoff = retryAfter is { } wait && wait > _options.QuotaBackoff ? wait : _options.QuotaBackoff;
        lock (_lock)
        {
            var until = _time.GetUtcNow() + backoff;
            if (until > _blockedUntil)
            {
                _blockedUntil = until;
            }

            return _blockedUntil;
        }
    }

    public void Dispose() => _bucket.Dispose();

    private void ThrowIfBlocked()
    {
        if (BlockedUntil is { } until)
        {
            throw new LgQuotaBlockedException(until);
        }
    }

    private void Trim(DateTimeOffset now)
    {
        while (_recent.Count > 0 && now - _recent.Peek() > TimeSpan.FromHours(1))
        {
            _recent.Dequeue();
        }
    }
}

/// <summary>
/// The budgets of every token this connector uses (a process-wide registry held by the factory, so a runtime restarted by
/// the supervisor — or a setup flow for the same token — keeps the same bucket and the same 1306 pause). Tokens are keyed
/// by their SHA-256, never stored.
/// </summary>
internal sealed class LgRateBudgets(LgRateOptions options) : IDisposable
{
    private readonly ConcurrentDictionary<string, LgRateBudget> _budgets = new(StringComparer.Ordinal);

    public LgRateBudget For(string pat, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrEmpty(pat);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pat)));
        return _budgets.GetOrAdd(key, _ => new LgRateBudget(time, options));
    }

    public void Dispose()
    {
        foreach (var budget in _budgets.Values)
        {
            budget.Dispose();
        }

        _budgets.Clear();
    }
}

/// <summary>
/// A token bucket on a <see cref="TimeProvider"/> (System.Threading.RateLimiting's own buckets replenish on the real
/// clock, which tests cannot move): <c>burst</c> tokens, one more every <c>period</c>; waiters are served one at a time in
/// arrival order.
/// </summary>
internal sealed class TimeProviderTokenBucket : RateLimiter
{
    private readonly TimeProvider _time;
    private readonly int _limit;
    private readonly TimeSpan _period;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly Lock _lock = new();
    private double _tokens;
    private long _last;
    private long _idleSince;
    private int _waiting;

    public TimeProviderTokenBucket(TimeProvider time, int burst, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(burst, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);
        _time = time;
        _limit = burst;
        _period = period;
        _tokens = burst;
        _last = _idleSince = time.GetTimestamp();
    }

    public override TimeSpan? IdleDuration
    {
        get
        {
            lock (_lock)
            {
                return _waiting > 0 ? null : _time.GetElapsedTime(_idleSince);
            }
        }
    }

    public override RateLimiterStatistics? GetStatistics()
    {
        lock (_lock)
        {
            Refill();
            return new RateLimiterStatistics { CurrentAvailablePermits = (long)_tokens, CurrentQueuedCount = _waiting };
        }
    }

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(permitCount, _limit);
        lock (_lock)
        {
            Refill();
            if (_waiting == 0 && _tokens >= permitCount)
            {
                _tokens -= permitCount;
                _idleSince = _time.GetTimestamp();
                return Lease.Acquired;
            }

            return Lease.Failed;
        }
    }

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(permitCount, _limit);
        lock (_lock)
        {
            _waiting++;
        }

        try
        {
            await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                while (true)
                {
                    TimeSpan wait;
                    lock (_lock)
                    {
                        Refill();
                        if (_tokens >= permitCount)
                        {
                            _tokens -= permitCount;
                            _idleSince = _time.GetTimestamp();
                            return Lease.Acquired;
                        }

                        wait = TimeSpan.FromTicks(Math.Max(1, (long)Math.Ceiling((permitCount - _tokens) * _period.Ticks)));
                    }

                    await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _turn.Release();
            }
        }
        finally
        {
            lock (_lock)
            {
                _waiting--;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _turn.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Refill()
    {
        var now = _time.GetTimestamp();
        var elapsed = _time.GetElapsedTime(_last, now);
        if (elapsed > TimeSpan.Zero)
        {
            _tokens = Math.Min(_limit, _tokens + (elapsed.Ticks / (double)_period.Ticks));
            _last = now;
        }
    }

    private sealed class Lease(bool acquired) : RateLimitLease
    {
        public static Lease Acquired { get; } = new(true);

        public static Lease Failed { get; } = new(false);

        public override bool IsAcquired { get; } = acquired;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }
}

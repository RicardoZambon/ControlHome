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
/// retries included; a user's command (<c>priority</c>) goes before the background calls waiting for their turn. Calls
/// during the pause fail at once with <see cref="LgQuotaBlockedException"/>.
/// </summary>
internal sealed class LgRateBudget : IDisposable
{
    /// <summary>The longest pause a 1306's <c>Retry-After</c> can ask for (a misbehaving server cannot park the token for weeks).</summary>
    public static readonly TimeSpan MaxQuotaBackoff = TimeSpan.FromDays(1);

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

    /// <summary>Waits for a token for one background HTTP attempt.</summary>
    /// <exception cref="LgQuotaBlockedException">Calls are paused after a 1306.</exception>
    public ValueTask AcquireAsync(CancellationToken cancellationToken) => AcquireAsync(priority: false, cancellationToken);

    /// <summary>
    /// Waits for a token for one HTTP attempt; a <paramref name="priority"/> call (a command) is served before every
    /// background call still waiting.
    /// </summary>
    /// <exception cref="LgQuotaBlockedException">Calls are paused after a 1306.</exception>
    public async ValueTask AcquireAsync(bool priority, CancellationToken cancellationToken)
    {
        ThrowIfBlocked();
        using var lease = await _bucket.AcquireAsync(1, priority, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// LG said the call limit is used up: pause every call for <see cref="LgRateOptions.QuotaBackoff"/> (or longer when LG
    /// says so, up to <see cref="MaxQuotaBackoff"/>).
    /// </summary>
    public DateTimeOffset Block(TimeSpan? retryAfter = null)
    {
        var backoff = retryAfter is { } wait && wait > _options.QuotaBackoff ? (wait < MaxQuotaBackoff ? wait : MaxQuotaBackoff) : _options.QuotaBackoff;
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
/// clock, which tests cannot move): <c>burst</c> tokens, one more every <c>period</c>. Waiters queue in arrival order,
/// priority waiters (commands) ahead of the others; one timer serves the queue as tokens come in.
/// </summary>
internal sealed class TimeProviderTokenBucket : RateLimiter
{
    /// <summary>Floating-point slack when comparing replenished tokens (a token due "now" must not wait another tick).</summary>
    private const double Epsilon = 1e-9;

    private readonly TimeProvider _time;
    private readonly int _limit;
    private readonly TimeSpan _period;
    private readonly Lock _lock = new();
    private readonly LinkedList<Waiter> _queue = new();
    private ITimer? _timer;
    private double _tokens;
    private long _last;
    private long _idleSince;
    private bool _disposed;

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
                return _queue.Count > 0 ? null : _time.GetElapsedTime(_idleSince);
            }
        }
    }

    public override RateLimiterStatistics? GetStatistics()
    {
        lock (_lock)
        {
            Refill();
            return new RateLimiterStatistics { CurrentAvailablePermits = (long)(_tokens + Epsilon), CurrentQueuedCount = _queue.Count };
        }
    }

    /// <summary>Waits for <paramref name="permitCount"/> tokens; <paramref name="priority"/> waiters go before the others.</summary>
    public ValueTask<RateLimitLease> AcquireAsync(int permitCount, bool priority, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(permitCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(permitCount, _limit);
        cancellationToken.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Refill();

            // Nobody it has to wait for: a background call waits for every waiter, a priority call only for other priority ones.
            if (Has(permitCount) && (priority ? _queue.First?.Value.Priority != true : _queue.Count == 0))
            {
                Take(permitCount);
                return ValueTask.FromResult<RateLimitLease>(Lease.Acquired);
            }

            waiter = new Waiter(permitCount, priority);
            if (priority)
            {
                var first = _queue.First;
                while (first is not null && first.Value.Priority)
                {
                    first = first.Next;
                }

                waiter.Node = first is null ? _queue.AddLast(waiter) : _queue.AddBefore(first, waiter);
            }
            else
            {
                waiter.Node = _queue.AddLast(waiter);
            }

            Pump();
        }

        return WaitAsync(waiter, cancellationToken);
    }

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(permitCount, _limit);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Refill();
            if (_queue.Count == 0 && Has(permitCount))
            {
                Take(permitCount);
                return Lease.Acquired;
            }

            return Lease.Failed;
        }
    }

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
        AcquireAsync(permitCount, priority: false, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            List<Waiter> waiting;
            lock (_lock)
            {
                _disposed = true;
                _timer?.Dispose();
                _timer = null;
                waiting = [.. _queue];
                _queue.Clear();
            }

            foreach (var waiter in waiting)
            {
                waiter.Node = null;
                waiter.Completion.TrySetException(new ObjectDisposedException(nameof(TimeProviderTokenBucket)));
            }
        }

        base.Dispose(disposing);
    }

    private async ValueTask<RateLimitLease> WaitAsync(Waiter waiter, CancellationToken cancellationToken)
    {
        await using var registration = cancellationToken.Register(
            static state =>
            {
                var (bucket, waiter, token) = ((TimeProviderTokenBucket, Waiter, CancellationToken))state!;
                bucket.Cancel(waiter, token);
            },
            (this, waiter, cancellationToken)).ConfigureAwait(false);
        return await waiter.Completion.Task.ConfigureAwait(false);
    }

    private void Cancel(Waiter waiter, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (waiter.Node is null)
            {
                return; // served (or disposed) already
            }

            _queue.Remove(waiter.Node);
            waiter.Node = null;
            if (!_disposed)
            {
                Pump();
            }
        }

        waiter.Completion.TrySetCanceled(cancellationToken);
    }

    /// <summary>Serves the queue head while tokens last and arms the timer for the next token (under the lock).</summary>
    private void Pump()
    {
        Refill();
        while (_queue.First is { } first && Has(first.Value.Permits))
        {
            _queue.RemoveFirst();
            first.Value.Node = null;
            Take(first.Value.Permits);
            first.Value.Completion.TrySetResult(Lease.Acquired);
        }

        if (_queue.First is { } next)
        {
            var due = TimeSpan.FromTicks(Math.Max(1, (long)Math.Ceiling((next.Value.Permits - _tokens) * _period.Ticks)));
            _timer ??= _time.CreateTimer(static state => ((TimeProviderTokenBucket)state!).OnTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(due, Timeout.InfiniteTimeSpan);
        }
        else
        {
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimer()
    {
        lock (_lock)
        {
            if (!_disposed)
            {
                Pump();
            }
        }
    }

    private bool Has(int permits) => _tokens + Epsilon >= permits;

    private void Take(int permits)
    {
        _tokens = Math.Max(0, _tokens - permits);
        _idleSince = _time.GetTimestamp();
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

    private sealed class Waiter(int permits, bool priority)
    {
        public int Permits { get; } = permits;

        public bool Priority { get; } = priority;

        /// <summary>The waiter's place in the queue; <c>null</c> once served, cancelled or failed.</summary>
        public LinkedListNode<Waiter>? Node { get; set; }

        public TaskCompletionSource<RateLimitLease> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

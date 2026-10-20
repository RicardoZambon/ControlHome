using System.Collections.Concurrent;

namespace ControlHome.Application.Security;

/// <summary>
/// In-memory step-up state per user: a grant (valid <see cref="GrantLifetime"/>, consumed by one safety-critical
/// command) and failed PIN attempts (<see cref="MaxFailures"/> within <see cref="FailureWindow"/> lock the user out of
/// safety-critical commands for <see cref="LockDuration"/>). Kept in memory on purpose: a grant must not outlive the
/// process, and a restart (which needs access to the server) also clears a lockout. PIN checks of one user run one at a
/// time (<see cref="BeginAttemptAsync"/>), so concurrent guesses cannot all slip in before the lockout starts.
/// </summary>
public sealed class SafetyGrantStore(TimeProvider timeProvider)
{
    public static readonly TimeSpan GrantLifetime = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);
    public const int MaxFailures = 5;

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, DateTimeOffset> _grants = [];
    private readonly Dictionary<Guid, List<DateTimeOffset>> _failures = [];
    private readonly Dictionary<Guid, DateTimeOffset> _lockedUntil = [];
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _attempts = new();

    /// <summary>
    /// Waits until no other PIN check of the user runs and returns a lease to dispose when done. Hold it across the whole
    /// check (<see cref="LockedFor"/>, verifying the PIN, then <see cref="RecordFailure"/> or <see cref="TryGrant"/>): a check
    /// waiting here sees the lockout an earlier one started and never verifies its PIN.
    /// </summary>
    public async Task<IDisposable> BeginAttemptAsync(Guid userId, CancellationToken cancellationToken)
    {
        var gate = _attempts.GetOrAdd(userId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new AttemptLease(gate);
    }

    /// <summary>
    /// Grants one safety-critical command within <see cref="GrantLifetime"/> and forgets earlier failures; false (nothing
    /// granted) while the user is locked out.
    /// </summary>
    public bool TryGrant(Guid userId)
    {
        lock (_lock)
        {
            var now = timeProvider.GetUtcNow();
            if (LockedForLocked(userId, now) is not null)
            {
                return false;
            }

            _grants[userId] = now + GrantLifetime;
            _failures.Remove(userId);
            return true;
        }
    }

    /// <summary>Forgets the user's grant, failures and lockout (an Admin reset the user's PIN).</summary>
    public void Forget(Guid userId)
    {
        lock (_lock)
        {
            _grants.Remove(userId);
            _failures.Remove(userId);
            _lockedUntil.Remove(userId);
        }
    }

    /// <summary>Uses the user's grant if it is still valid (true), so it cannot be used again.</summary>
    public bool TryConsume(Guid userId)
    {
        lock (_lock)
        {
            if (!_grants.Remove(userId, out var expires))
            {
                return false;
            }

            return expires > timeProvider.GetUtcNow();
        }
    }

    /// <summary>How long the user stays locked out, or <c>null</c> when not locked.</summary>
    public TimeSpan? LockedFor(Guid userId)
    {
        lock (_lock)
        {
            return LockedForLocked(userId, timeProvider.GetUtcNow());
        }
    }

    /// <summary>
    /// Counts a wrong PIN. Returns the lockout when this failure (or an earlier one) locked the user; a new lockout also
    /// drops any grant.
    /// </summary>
    public TimeSpan? RecordFailure(Guid userId)
    {
        lock (_lock)
        {
            var now = timeProvider.GetUtcNow();
            if (LockedForLocked(userId, now) is { } locked)
            {
                return locked;
            }

            if (!_failures.TryGetValue(userId, out var failures))
            {
                failures = [];
                _failures[userId] = failures;
            }

            failures.RemoveAll(at => now - at >= FailureWindow);
            failures.Add(now);
            if (failures.Count < MaxFailures)
            {
                return null;
            }

            _failures.Remove(userId);
            _grants.Remove(userId);
            _lockedUntil[userId] = now + LockDuration;
            return LockDuration;
        }
    }

    /// <summary>Forgets every grant, failure and lockout (test reset).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _grants.Clear();
            _failures.Clear();
            _lockedUntil.Clear();
        }
    }

    private TimeSpan? LockedForLocked(Guid userId, DateTimeOffset now)
    {
        if (!_lockedUntil.TryGetValue(userId, out var until))
        {
            return null;
        }

        if (until <= now)
        {
            _lockedUntil.Remove(userId);
            return null;
        }

        return until - now;
    }

    private sealed class AttemptLease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}

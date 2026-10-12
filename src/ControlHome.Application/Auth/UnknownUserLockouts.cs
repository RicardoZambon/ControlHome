namespace ControlHome.Application.Auth;

/// <summary>
/// Failed sign-ins for user names that do not exist, counted exactly like a real account's (same threshold, same escalating
/// <see cref="LockoutPolicy"/>), so a 423 <c>auth.locked_out</c> never reveals that a name exists. Kept in memory and
/// bounded: at most <see cref="MaxTrackedNames"/> names, each forgotten <see cref="Retention"/> after its last failure (or
/// earlier, oldest first, when the table is full).
/// </summary>
public sealed class UnknownUserLockouts(TimeProvider timeProvider)
{
    public const int MaxTrackedNames = 1024;

    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>How long <paramref name="normalizedUserName"/> stays locked out, or null when it is not locked out now.</summary>
    public TimeSpan? RemainingLockout(string normalizedUserName)
    {
        ArgumentNullException.ThrowIfNull(normalizedUserName);
        var now = timeProvider.GetUtcNow();
        lock (_lock)
        {
            return _entries.TryGetValue(normalizedUserName, out var entry) && entry.LockoutEnd > now ? entry.LockoutEnd - now : null;
        }
    }

    /// <summary>Counts a failed sign-in; returns the lockout it starts (every <paramref name="maxFailedAttempts"/>-th failure).</summary>
    /// <param name="baseLockout">The first lockout's length (Identity's <c>DefaultLockoutTimeSpan</c>).</param>
    public TimeSpan? RecordFailure(string normalizedUserName, int maxFailedAttempts, TimeSpan baseLockout)
    {
        ArgumentNullException.ThrowIfNull(normalizedUserName);
        var now = timeProvider.GetUtcNow();
        lock (_lock)
        {
            if (!_entries.TryGetValue(normalizedUserName, out var entry))
            {
                MakeRoom(now);
                entry = new Entry();
                _entries[normalizedUserName] = entry;
            }

            entry.LastFailure = now;
            entry.FailedCount++;
            if (entry.FailedCount < maxFailedAttempts)
            {
                return null;
            }

            var lockout = LockoutPolicy.Duration(baseLockout, entry.ConsecutiveLockouts);
            entry.LockoutEnd = now + lockout;
            entry.FailedCount = 0;
            entry.ConsecutiveLockouts++;
            return lockout;
        }
    }

    /// <summary>Forgets every name (Testing-only <c>POST /api/_test/reset</c>).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    private void MakeRoom(DateTimeOffset now)
    {
        if (_entries.Count < MaxTrackedNames)
        {
            return;
        }

        foreach (var (name, entry) in _entries)
        {
            if (now - entry.LastFailure >= Retention && entry.LockoutEnd <= now)
            {
                _entries.Remove(name);
            }
        }

        if (_entries.Count >= MaxTrackedNames)
        {
            // Still full (a flood of names): forget the one idle the longest, so memory stays bounded.
            _entries.Remove(_entries.MinBy(pair => pair.Value.LastFailure).Key);
        }
    }

    private sealed class Entry
    {
        public int FailedCount { get; set; }

        public int ConsecutiveLockouts { get; set; }

        public DateTimeOffset LockoutEnd { get; set; }

        public DateTimeOffset LastFailure { get; set; }
    }
}

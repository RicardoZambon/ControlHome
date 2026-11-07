namespace ControlHome.Connectors.SmartThings.Auth;

/// <summary>
/// What this process knows about each integration's token rotations: the newest pair it obtained and the refresh tokens
/// it spent to get there. SmartThings refresh tokens are single-use, so a stored pair whose refresh token this process
/// already spent is stale, and a run that reads one would only earn <c>invalid_grant</c> (and the user a needless
/// "sign in again"). That happens when a run could not save its rotated pair — the database was down, or the run was
/// being stopped while its refresh was in flight (the host refuses a stopping run's secret writes) — and when "Sign in
/// again" saved the pair SmartThings hands out for an already authorized location just after a refresh had spent it.
/// The next run then continues with the newest pair (and saves it); any other stored pair (new tokens from a
/// re-authorization) wins, and the remembered lineage is dropped. Shared by every run of the connector factory;
/// thread-safe. In memory only: a crash of the whole process between a refresh and its save still loses the new pair.
/// </summary>
internal sealed class TokenLineage
{
    /// <summary>Spent refresh tokens remembered per integration (a refresh is about daily).</summary>
    private const int SpentKept = 16;

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Entry> _entries = [];

    /// <summary>A refresh spent <paramref name="spentRefreshToken"/> and obtained <paramref name="next"/> (saved or not).</summary>
    public void Rotated(Guid integrationId, string spentRefreshToken, TokenSet next)
    {
        ArgumentNullException.ThrowIfNull(spentRefreshToken);
        ArgumentNullException.ThrowIfNull(next);
        lock (_lock)
        {
            if (!_entries.TryGetValue(integrationId, out var entry))
            {
                entry = new Entry();
                _entries[integrationId] = entry;
            }

            entry.Latest = next;
            entry.Spent.Enqueue(spentRefreshToken);
            while (entry.Spent.Count > SpentKept)
            {
                entry.Spent.Dequeue();
            }
        }
    }

    /// <summary>
    /// The pair a new run of <paramref name="integrationId"/> continues with: the newest one this process obtained when
    /// <paramref name="stored"/> holds a refresh token it already spent, else <paramref name="stored"/> itself.
    /// </summary>
    public TokenSet? Resolve(Guid integrationId, TokenSet? stored)
    {
        lock (_lock)
        {
            if (stored is null || !_entries.TryGetValue(integrationId, out var entry))
            {
                // Nothing stored means "sign in again" whatever this process remembers.
                return stored;
            }

            if (string.Equals(stored.RefreshToken, entry.Latest!.RefreshToken, StringComparison.Ordinal))
            {
                return stored;
            }

            if (entry.Spent.Contains(stored.RefreshToken, StringComparer.Ordinal))
            {
                return entry.Latest;
            }

            // New tokens from elsewhere (a re-authorization): they win; this lineage is history.
            _entries.Remove(integrationId);
            return stored;
        }
    }

    private sealed class Entry
    {
        public TokenSet? Latest { get; set; }

        public Queue<string> Spent { get; } = new();
    }
}

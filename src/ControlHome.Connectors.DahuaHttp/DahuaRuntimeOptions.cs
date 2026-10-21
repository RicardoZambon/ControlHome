namespace ControlHome.Connectors.DahuaHttp;

/// <summary>Timing of the Dahua runtime, its HTTP calls and the setup flow (tests shorten it).</summary>
/// <param name="RequestTimeout">One CGI call (headers and body), not the event stream.</param>
/// <param name="ConnectTimeout">Opening a TCP connection to the device.</param>
/// <param name="CommandTimeout">The door release: one request, never retried (the command service may cancel it sooner).</param>
/// <param name="HeartbeatTimeout">
/// The event stream is considered dead after this long without a byte (it asks for a heartbeat every 5 s).
/// </param>
/// <param name="ReconnectInitial">First delay after a failed or lost connection.</param>
/// <param name="ReconnectMax">Longest delay between attempts.</param>
/// <param name="ReconnectJitter">Relative jitter of each delay (0–0.5), so devices do not reconnect in lock-step.</param>
/// <param name="AuthRetryDelay">
/// After the device refused the credentials (401), the next try waits this long: devices lock a client out after a few
/// failed logins (often for 30 minutes), so a wrong password must never be retried quickly.
/// </param>
/// <param name="StableConnection">An event stream that stayed up this long resets the reconnect back-off.</param>
/// <param name="PulseHold">How long an instantaneous event (line crossing, a pulse) keeps <c>motion</c> true.</param>
internal sealed record DahuaRuntimeOptions(
    TimeSpan RequestTimeout,
    TimeSpan ConnectTimeout,
    TimeSpan CommandTimeout,
    TimeSpan HeartbeatTimeout,
    TimeSpan ReconnectInitial,
    TimeSpan ReconnectMax,
    double ReconnectJitter,
    TimeSpan AuthRetryDelay,
    TimeSpan StableConnection,
    TimeSpan PulseHold)
{
    /// <summary>The heartbeat interval asked of the device (<c>heartbeat=5</c>).</summary>
    public const int HeartbeatSeconds = 5;

    public static DahuaRuntimeOptions Default { get; } = new(
        RequestTimeout: TimeSpan.FromSeconds(10),
        ConnectTimeout: TimeSpan.FromSeconds(5),
        CommandTimeout: TimeSpan.FromSeconds(10),
        HeartbeatTimeout: TimeSpan.FromSeconds(20),
        ReconnectInitial: TimeSpan.FromSeconds(1),
        ReconnectMax: TimeSpan.FromMinutes(5),
        ReconnectJitter: 0.2,
        AuthRetryDelay: TimeSpan.FromMinutes(30),
        StableConnection: TimeSpan.FromMinutes(1),
        PulseHold: TimeSpan.FromSeconds(10));

    /// <summary>How long a command's idempotency key is remembered (a repeat within it is rejected as a duplicate).</summary>
    public TimeSpan IdempotencyWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Within one setup flow, how long after the device refused the credentials the user may try again (a double click
    /// must not count as two failed logins).
    /// </summary>
    public TimeSpan FlowAuthRetryCooldown { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Failed logins one setup flow may cause before it gives up (the device's lockout is near).</summary>
    public int FlowMaxAuthFailures { get; init; } = 3;

    /// <summary>The largest still accepted from <c>snapshot.cgi</c>.</summary>
    public int MaxSnapshotBytes { get; init; } = 10 * 1024 * 1024;
}

/// <summary>
/// Delays between connection attempts: <see cref="Initial"/> doubling per failed attempt up to <see cref="Max"/>, each
/// scaled by a random factor in [1 − <see cref="Jitter"/>, 1 + <see cref="Jitter"/>] (never above <see cref="Max"/>).
/// </summary>
internal sealed class ReconnectBackoff
{
    private readonly Random _random;
    private readonly Lock _lock = new();

    public ReconnectBackoff(TimeSpan initial, TimeSpan max, double jitter, Random? random = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(initial, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, initial);
        ArgumentOutOfRangeException.ThrowIfLessThan(jitter, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(jitter, 0.5);
        Initial = initial;
        Max = max;
        Jitter = jitter;
        _random = random ?? Random.Shared;
    }

    public TimeSpan Initial { get; }

    public TimeSpan Max { get; }

    public double Jitter { get; }

    /// <summary>The delay before attempt <paramref name="failedAttempts"/> + 1 (0 = after the first failure).</summary>
    public TimeSpan Next(int failedAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(failedAttempts);
        var exponential = Initial.Ticks * Math.Pow(2, Math.Min(failedAttempts, 30));
        var baseTicks = Math.Min(exponential, Max.Ticks);
        double factor;
        lock (_lock)
        {
            factor = 1 + (Jitter * ((2 * _random.NextDouble()) - 1));
        }

        return TimeSpan.FromTicks((long)Math.Min(baseTicks * factor, Max.Ticks));
    }
}

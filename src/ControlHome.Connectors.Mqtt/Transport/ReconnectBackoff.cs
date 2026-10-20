namespace ControlHome.Connectors.Mqtt.Transport;

/// <summary>
/// Delays between connection attempts: <see cref="Initial"/> doubling per failed attempt up to <see cref="Max"/>, each
/// scaled by a random factor in [1 − <see cref="Jitter"/>, 1 + <see cref="Jitter"/>] (never above <see cref="Max"/>), so
/// many clients do not reconnect in lock-step after a broker restart.
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

        // 2^30 × 1 s is far above any sane maximum: clamp the exponent so the arithmetic cannot overflow.
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

/// <summary>Timing of the MQTT runtime (tests shorten it).</summary>
/// <param name="ReconnectInitial">First delay after a failed or lost connection.</param>
/// <param name="ReconnectMax">Longest delay between attempts.</param>
/// <param name="ReconnectJitter">Relative jitter of each delay (0–0.5).</param>
/// <param name="ConnectTimeout">Timeout of one connection attempt (also the flow's connection test).</param>
/// <param name="ConnectionCheckInterval">How often an established connection is checked (besides the disconnect event).</param>
internal sealed record MqttRuntimeOptions(
    TimeSpan ReconnectInitial,
    TimeSpan ReconnectMax,
    double ReconnectJitter,
    TimeSpan ConnectTimeout,
    TimeSpan ConnectionCheckInterval)
{
    public static MqttRuntimeOptions Default { get; } = new(
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(60),
        0.2,
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(5));

    /// <summary>How long a command's idempotency key is remembered (a repeat within it is rejected as a duplicate).</summary>
    public TimeSpan IdempotencyWindow { get; init; } = TimeSpan.FromMinutes(10);
}

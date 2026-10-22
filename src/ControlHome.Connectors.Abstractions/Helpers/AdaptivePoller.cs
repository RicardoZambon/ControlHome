namespace ControlHome.Connectors.Abstractions;

/// <summary>
/// Paces a polling loop: polls every <see cref="Interval"/>, but every <see cref="HotInterval"/> during the
/// <see cref="HotWindow"/> after a command (so the user sees the result quickly), with random jitter so many devices do not
/// poll in lock-step. <see cref="NotifyCommand"/> also wakes a pending <see cref="WaitAsync"/> immediately.
/// </summary>
/// <example>
/// <code>
/// while (!ct.IsCancellationRequested) { await PollAsync(ct); await poller.WaitAsync(ct); }
/// // in ExecuteAsync, after sending the command:
/// poller.NotifyCommand();
/// </code>
/// </example>
public sealed class AdaptivePoller
{
    private readonly TimeProvider _time;
    private readonly Random _random;
    private readonly Lock _lock = new();
    private DateTimeOffset _hotUntil = DateTimeOffset.MinValue;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="interval">The normal polling interval.</param>
    /// <param name="hotInterval">The interval inside the hot window (shorter than <paramref name="interval"/>).</param>
    /// <param name="hotWindow">How long after a command to poll at <paramref name="hotInterval"/>.</param>
    /// <param name="jitter">Relative jitter, 0–0.5: each delay is scaled by a random factor in [1 − jitter, 1 + jitter].</param>
    /// <param name="random">Randomness for the jitter (seed it in tests); defaults to <see cref="Random.Shared"/>.</param>
    public AdaptivePoller(
        TimeProvider time,
        TimeSpan interval,
        TimeSpan hotInterval,
        TimeSpan hotWindow,
        double jitter = 0.1,
        Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(hotInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hotInterval, interval);
        ArgumentOutOfRangeException.ThrowIfLessThan(hotWindow, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(jitter, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(jitter, 0.5);

        _time = time;
        Interval = interval;
        HotInterval = hotInterval;
        HotWindow = hotWindow;
        Jitter = jitter;
        _random = random ?? Random.Shared;
    }

    public TimeSpan Interval { get; }

    public TimeSpan HotInterval { get; }

    public TimeSpan HotWindow { get; }

    public double Jitter { get; }

    /// <summary>Whether the hot window of the last command is still open.</summary>
    public bool IsHot
    {
        get
        {
            lock (_lock)
            {
                return _time.GetUtcNow() < _hotUntil;
            }
        }
    }

    /// <summary>Opens (or extends) the hot window from now and wakes a pending <see cref="WaitAsync"/>.</summary>
    public void NotifyCommand()
    {
        TaskCompletionSource wake;
        lock (_lock)
        {
            _hotUntil = _time.GetUtcNow() + HotWindow;
            wake = _wake;
            _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        wake.TrySetResult();
    }

    /// <summary>The next delay: <see cref="HotInterval"/> while <see cref="IsHot"/>, else <see cref="Interval"/>, with jitter applied.</summary>
    public TimeSpan NextDelay()
    {
        var baseDelay = IsHot ? HotInterval : Interval;
        if (Jitter == 0)
        {
            return baseDelay;
        }

        double factor;
        lock (_lock)
        {
            factor = 1 + (Jitter * ((2 * _random.NextDouble()) - 1));
        }

        return TimeSpan.FromTicks((long)(baseDelay.Ticks * factor));
    }

    /// <summary>Waits <see cref="NextDelay"/>, or less when <see cref="NotifyCommand"/> is called meanwhile.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        Task wake;
        lock (_lock)
        {
            wake = _wake.Task;
        }

        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(NextDelay(), _time, delayCancellation.Token);
        var finished = await Task.WhenAny(delay, wake).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (finished == wake)
        {
            await delayCancellation.CancelAsync().ConfigureAwait(false);
        }
    }
}

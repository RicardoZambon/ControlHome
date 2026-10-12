namespace ControlHome.Infrastructure.Devices;

/// <summary>Tuning of the device runtime (configuration section <c>DeviceRuntime</c>; tests shorten the intervals).</summary>
public sealed class DeviceRuntimeOptions
{
    public const string SectionName = "DeviceRuntime";

    /// <summary>Whether this process may run the device runtime at all (false: always idle, e.g. for tooling).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often pending state is written to the database (write-behind).</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// History rows kept while the database is unavailable; beyond this the oldest are dropped and history is reported as
    /// paused. Current state is never dropped.
    /// </summary>
    public int MaxPendingHistoryRows { get; set; } = 50_000;

    /// <summary>Distinct attributes waiting in the state queue; beyond this the oldest waiting report is dropped.</summary>
    public int QueueCapacity { get; set; } = 10_000;

    /// <summary>State deltas are pushed to clients in batches at most this far apart.</summary>
    public TimeSpan PublishWindow { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>How often a standby retries to take the runtime lease.</summary>
    public TimeSpan LeaseRetryInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How often the active process checks that it still holds the runtime lease.</summary>
    public TimeSpan LeaseCheckInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>First delay before restarting a failed connector runtime (doubles up to <see cref="RestartMaxDelay"/>).</summary>
    public TimeSpan RestartInitialDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan RestartMaxDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>A runtime that ran at least this long before failing restarts with <see cref="RestartInitialDelay"/> again.</summary>
    public TimeSpan RestartResetAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>A device's <c>LastSeenAtUtc</c> is persisted at most this often.</summary>
    public TimeSpan LastSeenPersistInterval { get; set; } = TimeSpan.FromSeconds(60);
}

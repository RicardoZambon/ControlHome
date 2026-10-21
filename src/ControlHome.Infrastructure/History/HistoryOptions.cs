namespace ControlHome.Infrastructure.History;

/// <summary>Tuning of the history rollups and the retention job (configuration section <c>History</c>).</summary>
public sealed class HistoryOptions
{
    public const string SectionName = "History";

    /// <summary>Whether the rollups and the retention job run on their own (tests that drive them directly turn this off).</summary>
    public bool MaintenanceEnabled { get; set; } = true;

    /// <summary>How often the rollups run (only while this process is the active runtime).</summary>
    public TimeSpan RollupInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How often the retention job runs (only while this process is the active runtime).</summary>
    public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// A bucket is rolled up only once it ended at least this long ago, so history rows still on their way (write-behind,
    /// late observations) land in it first.
    /// </summary>
    public TimeSpan SettleDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>The longest window of 5-minute buckets computed in one transaction (catch-up runs batch after batch).</summary>
    public TimeSpan ShortTermBatch { get; set; } = TimeSpan.FromHours(1);

    /// <summary>The longest window of hourly buckets computed in one transaction.</summary>
    public TimeSpan HourlyBatch { get; set; } = TimeSpan.FromDays(1);

    /// <summary>The longest window of hourly energy computed in one transaction.</summary>
    public TimeSpan EnergyBatch { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Power readings at most this far apart are integrated linearly (trapezoid); farther apart the earlier one is held.</summary>
    public TimeSpan PowerInterpolationLimit { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Rows deleted per statement by the retention job (<c>DELETE TOP (n)</c> loops keep transactions short).</summary>
    public int PurgeBatchSize { get; set; } = 5_000;

    /// <summary>Timeout of each rollup and purge statement.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMinutes(2);
}

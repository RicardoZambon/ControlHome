namespace ControlHome.Domain.History;

/// <summary>
/// Statistics of one numeric attribute over one bucket (primary key <c>(AttributeId, BucketStartUtc)</c>). Measurements
/// fill <see cref="Mean"/> (time-weighted), <see cref="Min"/> and <see cref="Max"/>; counters (<c>total</c>,
/// <c>total_increasing</c>) fill <see cref="Sum"/> (the increase in the bucket) and <see cref="LastCounter"/> (the reading
/// later increases count from). See <see cref="RollupMath"/>.
/// </summary>
public abstract class AttributeStatistic
{
    public int AttributeId { get; set; }

    /// <summary>The bucket's start (UTC, aligned to the bucket length).</summary>
    public DateTimeOffset BucketStartUtc { get; set; }

    public double? Mean { get; set; }

    public double? Min { get; set; }

    public double? Max { get; set; }

    public double? Sum { get; set; }

    public double? LastCounter { get; set; }
}

/// <summary>5-minute statistics (table <c>StatShortTerm</c>), kept <see cref="RetentionSettings.ShortTermStatsDays"/>.</summary>
public sealed class ShortTermStatistic : AttributeStatistic
{
    public static readonly TimeSpan BucketLength = TimeSpan.FromMinutes(5);
}

/// <summary>Hourly statistics (table <c>StatHourly</c>), kept forever.</summary>
public sealed class HourlyStatistic : AttributeStatistic
{
    public static readonly TimeSpan BucketLength = TimeSpan.FromHours(1);
}

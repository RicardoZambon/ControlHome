namespace ControlHome.Domain.History;

/// <summary>
/// How long history is kept (table <c>RetentionSettings</c>, a single row with <see cref="Id"/> 1; the defaults apply
/// until an Admin saves it). Hourly statistics and hourly energy are kept forever. Purges never delete rows a rollup
/// still needs (see <see cref="RollupProgress"/>).
/// </summary>
public sealed class RetentionSettings
{
    public const int SingletonId = 1;

    public const int DefaultStateLogDays = 10;
    public const int DefaultShortTermStatsDays = 10;
    public const int DefaultDeviceEventDays = 30;
    public const int DefaultAuditLogDays = 365;

    public int Id { get; set; } = SingletonId;

    /// <summary>Raw history (<c>StateLog</c>).</summary>
    public int StateLogDays { get; set; } = DefaultStateLogDays;

    /// <summary>5-minute statistics (<c>StatShortTerm</c>).</summary>
    public int ShortTermStatsDays { get; set; } = DefaultShortTermStatsDays;

    /// <summary>Momentary events (<c>DeviceEvent</c>).</summary>
    public int DeviceEventDays { get; set; } = DefaultDeviceEventDays;

    /// <summary>The audit log.</summary>
    public int AuditLogDays { get; set; } = DefaultAuditLogDays;

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

namespace ControlHome.Domain.History;

/// <summary>
/// How far a rollup has got (table <c>RollupProgress</c>, one row per rolled-up table): every bucket before
/// <see cref="WatermarkUtc"/> is final. Rollups resume from it, and purges never delete source rows at or after it.
/// </summary>
public sealed class RollupProgress
{
    /// <summary>5-minute statistics from <c>StateLog</c>.</summary>
    public const string ShortTerm = "StatShortTerm";

    /// <summary>Hourly statistics from <c>StatShortTerm</c>.</summary>
    public const string Hourly = "StatHourly";

    /// <summary>Hourly energy from <c>StatHourly</c> (counters) and <c>StateLog</c> (power).</summary>
    public const string Energy = "EnergyHourly";

    public const int NameMaxLength = 32;

    /// <summary>The rolled-up table (<see cref="ShortTerm"/>, <see cref="Hourly"/>, <see cref="Energy"/>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The end (exclusive) of the last finished bucket.</summary>
    public DateTimeOffset WatermarkUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

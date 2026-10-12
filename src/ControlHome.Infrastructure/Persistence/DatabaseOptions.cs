namespace ControlHome.Infrastructure.Persistence;

/// <summary>Database startup behaviour (configuration section <c>Database</c>).</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>How long to keep retrying the first connection before giving up (state <c>Failed</c>).</summary>
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Apply pending EF Core migrations at startup.</summary>
    public bool AutoMigrate { get; set; } = true;
}

namespace ControlHome.Infrastructure.Configuration;

/// <summary>Application paths (configuration section <c>ControlHome</c>).</summary>
public sealed class ControlHomeOptions
{
    public const string SectionName = "ControlHome";

    /// <summary>
    /// Runtime data folder (Data Protection certificate, logs, setup code, config); absolute, or relative to the content root.
    /// Default <c>/data</c> (the container volume); Development uses <c>../../.data</c> (repository root, gitignored).
    /// </summary>
    public string DataPath { get; set; } = "/data";

    /// <summary>Built SPA folder (absolute, or relative to the content root); <c>wwwroot</c> when not set.</summary>
    public string? SpaPath { get; set; }
}

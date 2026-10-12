using ControlHome.Application.Common;

namespace ControlHome.Application.Runtime;

/// <summary>Whether this process runs the device runtime (connectors, state pipeline, later the scheduler).</summary>
public enum RuntimeMode
{
    /// <summary>Waiting for the database, or deciding which process is active.</summary>
    Starting,

    /// <summary>This process holds the runtime lease: connectors and the state pipeline run here.</summary>
    Active,

    /// <summary>
    /// Another process holds the runtime lease (two instances on one database): this one is a read-only standby — no
    /// connectors, no commands, no scheduler — until the lease is free again.
    /// </summary>
    Standby,
}

/// <summary>The runtime's health (<c>GET /api/system/status</c>, hub <c>SystemStatusChanged</c>).</summary>
/// <param name="RuntimeMode"><c>Starting</c>, <c>Active</c> or <c>Standby</c> (show a banner when not Active).</param>
/// <param name="HistoryPaused">
/// The database could not keep up (or is down) and state history was dropped; current state and control keep working.
/// Cleared once the backlog is written.
/// </param>
/// <param name="Version">The application version.</param>
public sealed record SystemStatusDto(string RuntimeMode, bool HistoryPaused, string Version);

/// <summary>The runtime's current mode and health (implemented by Infrastructure).</summary>
public interface IRuntimeStatus
{
    RuntimeMode Mode { get; }

    bool HistoryPaused { get; }
}

public static class RuntimeStatusExtensions
{
    public static SystemStatusDto ToDto(this IRuntimeStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new SystemStatusDto(status.Mode.ToString(), status.HistoryPaused, AppVersion.Informational);
    }
}

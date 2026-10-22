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

    /// <summary>
    /// Whether this process runs a device runtime at all. Without one (none registered, or <c>DeviceRuntime:Enabled</c> is
    /// <c>false</c>) <see cref="Mode"/> stays <see cref="RuntimeMode.Starting"/> for good.
    /// </summary>
    bool Enabled { get; }
}

public static class RuntimeStatusExtensions
{
    public static SystemStatusDto ToDto(this IRuntimeStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new SystemStatusDto(status.Mode.ToString(), status.HistoryPaused, AppVersion.Informational);
    }

    /// <summary>
    /// Why an integration change (create, update, delete, a flow's result) must not be made in this process, or <c>null</c>.
    /// Integration changes are made only where they are applied: the active instance reads integrations only when it
    /// becomes active, so a change made on a standby would never reach it (a deleted integration would keep running with
    /// its credentials): 409 <c>runtime.standby</c>. While this process is still deciding (Starting, with a device runtime)
    /// it may yet become a standby: 503 <c>runtime.starting</c>. Without a device runtime nothing runs anywhere from here,
    /// so nothing is refused.
    /// </summary>
    public static AppError? IntegrationChangeRefusal(this IRuntimeStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Mode switch
        {
            RuntimeMode.Standby => AppError.Conflict(
                ErrorCodes.RuntimeStandby,
                "This instance is a read-only standby; make the change on the instance that runs the devices."),
            RuntimeMode.Starting when status.Enabled => AppError.Unavailable(
                ErrorCodes.RuntimeStarting,
                "The device runtime is starting; try again in a moment."),
            _ => null,
        };
    }
}

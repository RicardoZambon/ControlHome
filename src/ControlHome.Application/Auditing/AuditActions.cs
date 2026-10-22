namespace ControlHome.Application.Auditing;

/// <summary>Stable audit action names (dotted, like error codes).</summary>
public static class AuditActions
{
    public const string SetupCompleted = "setup.completed";

    public const string LoginSucceeded = "auth.login.succeeded";
    public const string LoginFailed = "auth.login.failed";
    public const string LoginLockedOut = "auth.login.locked_out";
    public const string Logout = "auth.logout";
    public const string PasswordChanged = "auth.password.changed";
    public const string PasswordChangeFailed = "auth.password.change_failed";

    /// <summary>A wrong (or locked-out) password when re-authenticating for a sensitive change, e.g. the safety PIN.</summary>
    public const string PasswordVerifyFailed = "auth.password.verify_failed";
    public const string AdminReset = "auth.admin_reset";

    public const string UserCreated = "users.created";
    public const string UserUpdated = "users.updated";
    public const string UserDeleted = "users.deleted";
    public const string UserPasswordReset = "users.password_reset";

    public const string HomeUpdated = "home.updated";

    /// <summary>The demo home was loaded (setup option, <c>--seed-demo</c>, test hook).</summary>
    public const string DemoLoaded = "demo.loaded";

    /// <summary>An Admin downloaded a backup (whether it holds secrets; never the passphrase).</summary>
    public const string BackupExported = "backup.exported";
    public const string BackupImported = "backup.imported";

    /// <summary>An Admin changed how long history is kept.</summary>
    public const string RetentionSettingsUpdated = "settings.retention_updated";

    public const string FloorCreated = "floors.created";
    public const string FloorUpdated = "floors.updated";
    public const string FloorDeleted = "floors.deleted";
    public const string FloorLayoutSaved = "floors.layout_saved";

    public const string FileUploaded = "files.uploaded";

    public const string PlacementSaved = "placements.saved";
    public const string PlacementDeleted = "placements.deleted";

    public const string IntegrationCreated = "integrations.created";

    /// <summary>Renamed, enabled/disabled, or re-configured / re-authorized by a flow (secret field names only).</summary>
    public const string IntegrationUpdated = "integrations.updated";
    public const string IntegrationDeleted = "integrations.deleted";

    public const string DeviceUpdated = "devices.updated";

    /// <summary>A device's settings, placements and history moved to another device; the old one was deleted.</summary>
    public const string DeviceReplaced = "devices.replaced";
    public const string EndpointUpdated = "endpoints.updated";
    public const string EndpointFavoriteChanged = "endpoints.favorite_changed";

    /// <summary>A device command finished (outcome confirmed, failed, timed_out or superseded).</summary>
    public const string CommandExecuted = "commands.executed";

    public const string SafetyPinSet = "safety.pin.set";

    /// <summary>An Admin cleared a user's safety PIN (the user sets a new one with their password).</summary>
    public const string SafetyPinReset = "safety.pin.reset";
    public const string SafetyStepUpSucceeded = "safety.step_up.succeeded";
    public const string SafetyStepUpFailed = "safety.step_up.failed";
    public const string SafetyStepUpLocked = "safety.step_up.locked";

    public const string NetworkDenied = "security.network_denied";
    public const string HostDenied = "security.host_denied";
}

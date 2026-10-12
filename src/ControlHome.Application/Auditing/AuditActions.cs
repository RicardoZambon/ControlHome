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
    public const string AdminReset = "auth.admin_reset";

    public const string UserCreated = "users.created";
    public const string UserUpdated = "users.updated";
    public const string UserDeleted = "users.deleted";
    public const string UserPasswordReset = "users.password_reset";

    public const string HomeUpdated = "home.updated";

    public const string FloorCreated = "floors.created";
    public const string FloorUpdated = "floors.updated";
    public const string FloorDeleted = "floors.deleted";
    public const string FloorLayoutSaved = "floors.layout_saved";

    public const string FileUploaded = "files.uploaded";

    public const string IntegrationCreated = "integrations.created";
    public const string IntegrationDeleted = "integrations.deleted";

    public const string DeviceUpdated = "devices.updated";
    public const string EndpointUpdated = "endpoints.updated";
    public const string EndpointFavoriteChanged = "endpoints.favorite_changed";

    /// <summary>A device command finished (outcome confirmed, failed, timed_out or superseded).</summary>
    public const string CommandExecuted = "commands.executed";

    public const string SafetyPinSet = "safety.pin.set";
    public const string SafetyStepUpSucceeded = "safety.step_up.succeeded";
    public const string SafetyStepUpFailed = "safety.step_up.failed";
    public const string SafetyStepUpLocked = "safety.step_up.locked";

    public const string NetworkDenied = "security.network_denied";
}

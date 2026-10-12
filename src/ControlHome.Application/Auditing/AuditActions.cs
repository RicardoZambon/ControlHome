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

    public const string NetworkDenied = "security.network_denied";
}

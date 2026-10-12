namespace ControlHome.Application.Common;

/// <summary>
/// Stable, dotted error codes carried in the ProblemDetails <c>code</c> extension. The SPA maps each one to the i18n key
/// <c>errors.&lt;code&gt;</c>, so never rename a code; add a new one instead.
/// </summary>
public static class ErrorCodes
{
    // Request edge and framework-level failures
    public const string RequestNotFound = "request.not_found";
    public const string RequestInvalid = "request.invalid";
    public const string RequestMethodNotAllowed = "request.method_not_allowed";
    public const string RequestRateLimited = "request.rate_limited";
    public const string RequestCrossOrigin = "request.cross_origin";
    public const string RequestMissingHeader = "request.missing_header";
    public const string RequestNetworkDenied = "request.network_denied";
    public const string ValidationFailed = "validation.failed";
    public const string InternalError = "internal.error";

    // Database
    public const string DbUnavailable = "db.unavailable";
    public const string DbAlreadyConfigured = "db.already_configured";
    public const string DbConnectionFailed = "db.connection_failed";
    public const string DbCreateDenied = "db.create_denied";
    public const string DbMigrationFailed = "db.migration_failed";
    public const string DbMigrationsPending = "db.migrations_pending";
    public const string SqlVersionUnsupported = "sql.version_unsupported";
    public const string SqlCompatibilityUnsupported = "sql.compatibility_unsupported";

    // First-run setup
    public const string SetupInvalidCode = "setup.invalid_code";
    public const string SetupAlreadyCompleted = "setup.already_completed";
    public const string SetupNotCompleted = "setup.not_completed";

    // Authentication
    public const string AuthRequired = "auth.required";
    public const string AuthForbidden = "auth.forbidden";
    public const string AuthInvalidCredentials = "auth.invalid_credentials";
    public const string AuthLockedOut = "auth.locked_out";
    public const string AuthDisabled = "auth.disabled";
    public const string AuthPasswordIncorrect = "auth.password_incorrect";
    public const string AuthResetInvalid = "auth.reset_invalid";

    // User administration
    public const string UsersNotFound = "users.not_found";
    public const string UsersLastAdmin = "users.last_admin";
    public const string UsersCannotDeleteSelf = "users.cannot_delete_self";
    public const string UsersDuplicateUserName = "users.duplicate_username";
}

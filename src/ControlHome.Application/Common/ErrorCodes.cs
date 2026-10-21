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
    public const string RequestHostDenied = "request.host_denied";
    public const string RequestConflict = "request.conflict";
    public const string RequestTooLarge = "request.too_large";
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

    // Floors and rooms
    public const string FloorsNotFound = "floors.not_found";
    public const string FloorsConcurrency = "floors.concurrency";
    public const string RoomsNotFound = "rooms.not_found";

    // Floor-plan geometry: field errors (e.g. errors["rooms[2].polygon"]) of a validation.failed problem
    public const string GeometryTooFewPoints = "geometry.too_few_points";
    public const string GeometryTooManyPoints = "geometry.too_many_points";
    public const string GeometrySelfIntersecting = "geometry.self_intersecting";
    public const string GeometryOutOfBounds = "geometry.out_of_bounds";
    public const string GeometryZeroArea = "geometry.zero_area";

    // Uploaded files
    public const string FilesNotFound = "files.not_found";
    public const string FilesUnsupportedType = "files.unsupported_type";
    public const string FilesTooLarge = "files.too_large";
    public const string FilesInvalidImage = "files.invalid_image";

    // Devices and integrations
    public const string DevicesNotFound = "devices.not_found";
    public const string EndpointsNotFound = "endpoints.not_found";
    public const string IntegrationsNotFound = "integrations.not_found";

    // Commands: 400 commands.invalid_arguments carries field errors errors["arguments.<key>"] with the commands.argument_* codes
    public const string CommandsInvalidArguments = "commands.invalid_arguments";
    public const string CommandsArgumentRequired = "commands.argument_required";
    public const string CommandsArgumentUnknown = "commands.argument_unknown";
    public const string CommandsArgumentInvalidType = "commands.argument_invalid_type";
    public const string CommandsArgumentOutOfRange = "commands.argument_out_of_range";
    public const string CommandsArgumentNotOnStep = "commands.argument_not_on_step";
    public const string CommandsArgumentNotAllowed = "commands.argument_not_allowed";
    public const string CommandsArgumentTooLong = "commands.argument_too_long";

    // Commands: request failures (ProblemDetails) and CommandStatus failure codes (hub)
    public const string CommandsUnsupported = "commands.unsupported";
    public const string CommandsDuplicate = "commands.duplicate";
    public const string CommandsGateClosed = "commands.gate_closed";
    public const string CommandsUnavailable = "commands.unavailable";
    public const string CommandsRefused = "commands.refused";
    public const string CommandsTimedOut = "commands.timed_out";
    public const string CommandsFailed = "commands.failed";

    /// <summary>Hub CommandStatus: the device reported a state that means the command did not happen (e.g. a jammed lock).</summary>
    public const string CommandsNotConfirmed = "commands.not_confirmed";

    // Safety-critical commands (step-up PIN)
    public const string SafetyStepUpRequired = "safety.step_up_required";
    public const string SafetyLocked = "safety.locked";
    public const string SafetyNotAllowed = "safety.not_allowed";
    public const string SafetyPinNotSet = "safety.pin_not_set";
    public const string SafetyPinIncorrect = "safety.pin_incorrect";
    public const string SafetyPasswordRequired = "safety.password_required";

    // Connectors and integrations
    public const string ConnectorsNotFound = "connectors.not_found";
    public const string IntegrationsInvalidConfig = "integrations.invalid_config";

    // Device runtime: this process is a read-only standby (another instance on the same database runs the devices), or is
    // still deciding whether it runs them
    public const string RuntimeStandby = "runtime.standby";
    public const string RuntimeStarting = "runtime.starting";
}

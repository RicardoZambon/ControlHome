namespace ControlHome.Application.Common;

/// <summary>
/// An expected, user-facing failure. <paramref name="Code"/> is a stable dotted identifier from <see cref="ErrorCodes"/>
/// (the SPA translates it as <c>errors.&lt;code&gt;</c>); <paramref name="Message"/> is an English fallback for logs and
/// API consumers; <paramref name="Extensions"/> become extra ProblemDetails members (e.g. <c>retryAfterSeconds</c>).
/// </summary>
public sealed record AppError(
    string Code,
    string Message,
    AppErrorKind Kind,
    IReadOnlyDictionary<string, object?>? Extensions = null)
{
    /// <summary>The extension holding field errors (same shape as a FluentValidation <c>validation.failed</c> problem).</summary>
    public const string ErrorsExtension = "errors";

    public static AppError Validation(string code, string message, IReadOnlyDictionary<string, object?>? extensions = null) =>
        new(code, message, AppErrorKind.Validation, extensions);

    /// <summary>
    /// 400 <c>validation.failed</c> with an <c>errors</c> dictionary keyed by camelCase field path (e.g.
    /// <c>rooms[2].polygon</c>), for checks that need the database and so cannot run in a FluentValidation validator.
    /// </summary>
    public static AppError FieldErrors(IReadOnlyDictionary<string, string[]> errors) =>
        Validation(
            ErrorCodes.ValidationFailed,
            "One or more fields are invalid.",
            new Dictionary<string, object?>(StringComparer.Ordinal) { [ErrorsExtension] = errors });

    public static AppError NotFound(string code, string message) => new(code, message, AppErrorKind.NotFound);

    public static AppError Conflict(string code, string message, IReadOnlyDictionary<string, object?>? extensions = null) =>
        new(code, message, AppErrorKind.Conflict, extensions);

    public static AppError Forbidden(string code, string message) => new(code, message, AppErrorKind.Forbidden);

    public static AppError Unauthorized(string code, string message) => new(code, message, AppErrorKind.Unauthorized);

    public static AppError Locked(string code, string message, IReadOnlyDictionary<string, object?>? extensions = null) =>
        new(code, message, AppErrorKind.Locked, extensions);

    public static AppError Unavailable(string code, string message, IReadOnlyDictionary<string, object?>? extensions = null) =>
        new(code, message, AppErrorKind.Unavailable, extensions);
}

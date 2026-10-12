namespace ControlHome.Application.Common;

/// <summary>The category of an <see cref="AppError"/>; the Api maps it to an HTTP status code.</summary>
public enum AppErrorKind
{
    /// <summary>The input is invalid (400).</summary>
    Validation,

    /// <summary>The target does not exist (404).</summary>
    NotFound,

    /// <summary>The request conflicts with the current state (409).</summary>
    Conflict,

    /// <summary>The caller is authenticated but not allowed to do this (403).</summary>
    Forbidden,

    /// <summary>The caller is not authenticated or the credentials are wrong (401).</summary>
    Unauthorized,

    /// <summary>The target is temporarily locked (423).</summary>
    Locked,

    /// <summary>A dependency (e.g. the database) is not available right now (503).</summary>
    Unavailable,
}

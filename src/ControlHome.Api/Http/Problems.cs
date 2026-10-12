using ControlHome.Application.Common;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ControlHome.Api.Http;

/// <summary>
/// The one mapping from <see cref="AppError"/> to HTTP: RFC 7807 ProblemDetails with the extension <c>code</c> (plus the
/// error's own extensions) and a status code chosen by <see cref="AppErrorKind"/>. <c>traceId</c> is added centrally by
/// the ProblemDetails customization.
/// </summary>
public static class Problems
{
    public const string CodeExtension = "code";

    public static int StatusCodeFor(AppErrorKind kind) => kind switch
    {
        AppErrorKind.Validation => StatusCodes.Status400BadRequest,
        AppErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
        AppErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        AppErrorKind.NotFound => StatusCodes.Status404NotFound,
        AppErrorKind.Conflict => StatusCodes.Status409Conflict,
        AppErrorKind.Locked => StatusCodes.Status423Locked,
        AppErrorKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    public static ProblemHttpResult From(AppError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var extensions = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (error.Extensions is not null)
        {
            foreach (var (key, value) in error.Extensions)
            {
                extensions[key] = value;
            }
        }

        extensions[CodeExtension] = error.Code;
        return TypedResults.Problem(detail: error.Message, statusCode: StatusCodeFor(error.Kind), extensions: extensions);
    }

    public static ProblemHttpResult ToProblem(this AppError error) => From(error);

    /// <summary>200 with the value, or the error's ProblemDetails.</summary>
    public static Results<Ok<T>, ProblemHttpResult> ToHttpResult<T>(this Result<T> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsSuccess)
        {
            return TypedResults.Ok(result.Value);
        }

        return From(result.Error);
    }

    /// <summary>204, or the error's ProblemDetails.</summary>
    public static Results<NoContent, ProblemHttpResult> ToHttpResult(this Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsSuccess)
        {
            return TypedResults.NoContent();
        }

        return From(result.Error);
    }
}

using System.Diagnostics;
using ControlHome.Api.Http;
using ControlHome.Application.Common;
using Microsoft.AspNetCore.Mvc;

namespace ControlHome.Api.Hosting;

public static class ProblemDetailsSetup
{
    public const string TraceIdExtension = "traceId";

    /// <summary>
    /// Every error response is ProblemDetails with <c>code</c> and <c>traceId</c>. Responses that carry no code get one from
    /// their status (404 <c>request.not_found</c>, 5xx <c>internal.error</c>, ...). Exception details never leave Development.
    /// </summary>
    public static IServiceCollection AddControlHomeProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            var problem = context.ProblemDetails;
            var status = problem.Status ?? context.HttpContext.Response.StatusCode;

            if (!problem.Extensions.ContainsKey(Problems.CodeExtension))
            {
                problem.Extensions[Problems.CodeExtension] = DefaultCode(status, problem);
            }

            problem.Extensions[TraceIdExtension] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;

            if (context.Exception is not null
                && !context.HttpContext.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment())
            {
                problem.Detail = null;
            }
        });

        return services;
    }

    private static string DefaultCode(int status, ProblemDetails problem) => status switch
    {
        StatusCodes.Status400BadRequest when problem is HttpValidationProblemDetails => ErrorCodes.ValidationFailed,
        StatusCodes.Status401Unauthorized => ErrorCodes.AuthRequired,
        StatusCodes.Status403Forbidden => ErrorCodes.AuthForbidden,
        StatusCodes.Status404NotFound => ErrorCodes.RequestNotFound,
        StatusCodes.Status405MethodNotAllowed => ErrorCodes.RequestMethodNotAllowed,
        StatusCodes.Status429TooManyRequests => ErrorCodes.RequestRateLimited,
        >= 500 => ErrorCodes.InternalError,
        _ => ErrorCodes.RequestInvalid,
    };
}

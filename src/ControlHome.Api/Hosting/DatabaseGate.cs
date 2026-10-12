using System.Globalization;
using ControlHome.Api.Features.Setup;
using ControlHome.Api.Http;
using ControlHome.Application.Common;
using ControlHome.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ControlHome.Api.Hosting;

/// <summary>
/// While the database is not <see cref="DatabaseState.Ready"/>, every <c>/api/*</c> request except
/// <c>/api/setup/status</c> and <c>/api/setup/database</c> (pre-database setup) gets 503 ProblemDetails
/// <c>db.unavailable</c> (with <c>state</c>) instead of an exception.
/// </summary>
internal sealed class DatabaseGateMiddleware(RequestDelegate next, DatabaseReadiness readiness)
{
    private const int RetryAfterSeconds = 5;

    public Task InvokeAsync(HttpContext context)
    {
        if (readiness.IsReady || !IsGated(context.Request.Path))
        {
            return next(context);
        }

        context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        var error = AppError.Unavailable(
            ErrorCodes.DbUnavailable,
            "The database is not available yet.",
            new Dictionary<string, object?> { ["state"] = readiness.State.ToString() });
        return Problems.From(error).ExecuteAsync(context);
    }

    private static bool IsGated(PathString path) =>
        path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWithSegments(SetupEndpoints.StatusPath, StringComparison.OrdinalIgnoreCase)
        && !path.StartsWithSegments(SetupEndpoints.DatabasePath, StringComparison.OrdinalIgnoreCase);
}

/// <summary><c>/health/ready</c>: healthy only when the database is <see cref="DatabaseState.Ready"/>.</summary>
internal sealed class DatabaseReadinessHealthCheck(DatabaseReadiness readiness) : IHealthCheck
{
    public const string ReadyTag = "ready";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(readiness.IsReady
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy($"Database state: {readiness.State}"));
}

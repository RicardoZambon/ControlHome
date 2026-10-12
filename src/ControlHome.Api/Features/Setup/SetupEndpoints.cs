using ControlHome.Api.Hosting;
using ControlHome.Api.Http;
using ControlHome.Application.Auth;
using ControlHome.Application.Setup;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ControlHome.Api.Features.Setup;

/// <summary>First-run setup (anonymous).</summary>
public static class SetupEndpoints
{
    public const string StatusPath = "/api/setup/status";
    public const string DatabasePath = "/api/setup/database";

    public static IEndpointRouteBuilder MapSetupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/setup").WithTags("Setup").AllowAnonymous();

        group.MapGet("/status", GetStatusAsync)
            .WithName("getSetupStatus")
            .WithSummary("First-run state; answers even while the database is not ready.");

        group.MapPost("/database", ConfigureDatabaseAsync)
            .WithName("configureDatabase")
            .WithSummary("Pre-database setup: tests the SQL Server connection, saves it (encrypted) and starts the database initialization.")
            .WithDescription(
                "Only while no database connection is configured (else 409 db.already_configured); answers even while the database is not ready. " +
                "400: validation.failed, setup.invalid_code, db.connection_failed (extension reason), sql.version_unsupported or sql.compatibility_unsupported; " +
                "409: db.create_denied (extension script: T-SQL for an administrator) or db.already_configured. " +
                "202: saved; poll GET /api/setup/status until databaseState is Ready.")
            .RequireRateLimiting(RateLimitPolicies.Setup)
            .WithValidation<DatabaseSetupRequest>() // 400: validation.failed
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/complete", CompleteAsync)
            .WithName("completeSetup")
            .WithSummary("Creates the first Admin and the home, then signs the admin in.")
            .RequireRateLimiting(RateLimitPolicies.Setup)
            .WithValidation<CompleteSetupRequest>() // 400: validation.failed or setup.invalid_code
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return app;
    }

    private static async Task<Ok<SetupStatusDto>> GetStatusAsync(SetupService setup, CancellationToken cancellationToken) =>
        TypedResults.Ok(await setup.GetStatusAsync(cancellationToken));

    private static async Task<Results<Accepted<SetupStatusDto>, ProblemHttpResult>> ConfigureDatabaseAsync(
        DatabaseSetupRequest request,
        DatabaseSetupService databaseSetup,
        CancellationToken cancellationToken)
    {
        var result = await databaseSetup.ConfigureAsync(request, cancellationToken);
        return result.IsSuccess
            ? TypedResults.Accepted(StatusPath, result.Value)
            : Problems.From(result.Error);
    }

    private static async Task<Results<Ok<MeDto>, ProblemHttpResult>> CompleteAsync(
        CompleteSetupRequest request,
        SetupService setup,
        CancellationToken cancellationToken) =>
        (await setup.CompleteAsync(request, cancellationToken)).ToHttpResult();
}

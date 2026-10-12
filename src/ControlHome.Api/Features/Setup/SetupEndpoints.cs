using ControlHome.Api.Hosting;
using ControlHome.Api.Http;
using ControlHome.Application.Auth;
using ControlHome.Application.Setup;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ControlHome.Api.Features.Setup;

/// <summary>First-run setup (anonymous).</summary>
public static class SetupEndpoints
{
    public static IEndpointRouteBuilder MapSetupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/setup").WithTags("Setup").AllowAnonymous();

        group.MapGet("/status", GetStatusAsync)
            .WithName("getSetupStatus")
            .WithSummary("First-run state; answers even while the database is not ready.");

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

    private static async Task<Results<Ok<MeDto>, ProblemHttpResult>> CompleteAsync(
        CompleteSetupRequest request,
        SetupService setup,
        CancellationToken cancellationToken) =>
        (await setup.CompleteAsync(request, cancellationToken)).ToHttpResult();
}

using ControlHome.Api.Hosting;
using ControlHome.Application.Runtime;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ControlHome.Api.Features.SystemStatus;

/// <summary>The runtime's health (active or standby, history paused) for the app's status banner.</summary>
public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/system")
            .WithTags("System")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .MapGet("/status", GetStatus)
            .WithName("getSystemStatus")
            .WithSummary("Runtime mode (Starting, Active, Standby), whether history is paused, and the app version.")
            .RequireAuthorization(AuthorizationPolicies.Viewer);

        return app;
    }

    private static Ok<SystemStatusDto> GetStatus(IRuntimeStatus status) => TypedResults.Ok(status.ToDto());
}

using ControlHome.Api.Hosting;
using ControlHome.Application.Dashboard;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ControlHome.Api.Features.Dashboard;

/// <summary>The dashboard summary: anyone signed in.</summary>
public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/dashboard")
            .WithTags("Dashboard")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .MapGet("", GetAsync)
            .WithName("getDashboard")
            .WithSummary("The home at a glance: favorites, rooms (active devices, temperature, power), power now, offline devices, integrations.")
            .WithDescription(
                "Values are current when served. Keep them live by applying hub StateChanged deltas to the listed attribute " +
                "ids (rooms[].temperatureAttributeIds / powerAttributeIds / activityAttributes, power.attributeIds) and " +
                "refetch on DevicesChanged and IntegrationStatusChanged.")
            .RequireAuthorization(AuthorizationPolicies.Viewer);

        return app;
    }

    private static async Task<Ok<DashboardDto>> GetAsync(DashboardService dashboard, CancellationToken cancellationToken) =>
        TypedResults.Ok(await dashboard.GetAsync(cancellationToken));
}

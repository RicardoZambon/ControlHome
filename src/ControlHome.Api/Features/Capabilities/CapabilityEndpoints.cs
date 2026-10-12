using ControlHome.Api.Hosting;
using ControlHome.Application.Capabilities;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ControlHome.Api.Features.Capabilities;

/// <summary>The capability catalogue (defined in code).</summary>
public static class CapabilityEndpoints
{
    public static IEndpointRouteBuilder MapCapabilityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/capabilities")
            .WithTags("Capabilities")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .MapGet("", List)
            .WithName("listCapabilities")
            .WithSummary("Every capability definition: attributes (schemas, units, state classes) and commands (arguments, safety flags).")
            .RequireAuthorization(AuthorizationPolicies.Viewer);

        return app;
    }

    private static Ok<IReadOnlyList<CapabilityDto>> List() => TypedResults.Ok(CapabilityMapping.Catalog);
}

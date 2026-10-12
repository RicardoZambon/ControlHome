using ControlHome.Api.Hosting;
using ControlHome.Api.Http;
using ControlHome.Application.Homes;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ControlHome.Api.Features.Home;

/// <summary>The home's settings.</summary>
public static class HomeEndpoints
{
    public static IEndpointRouteBuilder MapHomeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/home")
            .WithTags("Home")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("", GetAsync)
            .WithName("getHome")
            .WithSummary("The home's settings.")
            .RequireAuthorization(AuthorizationPolicies.Viewer)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("", UpdateAsync)
            .WithName("updateHome")
            .WithSummary("Changes the home's settings.")
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .WithValidation<UpdateHomeRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<Results<Ok<HomeDto>, ProblemHttpResult>> GetAsync(HomeService homes, CancellationToken cancellationToken) =>
        (await homes.GetAsync(cancellationToken)).ToHttpResult();

    private static async Task<Results<Ok<HomeDto>, ProblemHttpResult>> UpdateAsync(
        UpdateHomeRequest request,
        HomeService homes,
        CancellationToken cancellationToken) =>
        (await homes.UpdateAsync(request, cancellationToken)).ToHttpResult();
}

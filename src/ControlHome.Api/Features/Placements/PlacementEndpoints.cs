using System.ComponentModel;
using ControlHome.Api.Hosting;
using ControlHome.Api.Http;
using ControlHome.Application.Placements;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace ControlHome.Api.Features.Placements;

/// <summary>Endpoints and devices on the floor plans: anyone signed in reads, Admins place, move and remove.</summary>
public static class PlacementEndpoints
{
    public static IEndpointRouteBuilder MapPlacementEndpoints(this IEndpointRouteBuilder app)
    {
        var placements = app.MapGroup("/api/placements")
            .WithTags("Floors")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        placements.MapGet("", ListAsync)
            .WithName("listPlacements")
            .WithSummary("Every endpoint and device on the floor plans (optionally of one floor), with the room each is in.")
            .RequireAuthorization(AuthorizationPolicies.Viewer);

        placements.MapPut("", PlaceAsync)
            .WithName("placeOnFloor")
            .WithSummary("Places an endpoint or a whole device on a floor (or moves it); its room becomes the room at that point.")
            .WithDescription(
                "Exactly one of endpointId and deviceId. The target's room is set to the room whose outline contains the " +
                "point, or cleared when it is outside every room (an endpoint then shows in its device's room). 400 " +
                "validation.failed with errors.floorId = floors.not_found, errors.endpointId = endpoints.not_found, " +
                "errors.deviceId = devices.not_found, or errors.xCm / errors.yCm = geometry.out_of_bounds.")
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .WithValidation<PlaceRequest>();

        placements.MapDelete("/{id:int}", DeleteAsync)
            .WithName("deletePlacement")
            .WithSummary("Takes an endpoint or device off the plan (its room is kept).")
            .WithDescription("404 placements.not_found.")
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<Ok<IReadOnlyList<PlacementDto>>> ListAsync(
        [FromQuery, Description("Only the placements of this floor.")] int? floorId,
        PlacementService placements,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await placements.ListAsync(floorId, cancellationToken));

    private static async Task<Results<Ok<PlacementDto>, ProblemHttpResult>> PlaceAsync(
        PlaceRequest request,
        PlacementService placements,
        CancellationToken cancellationToken) =>
        (await placements.PlaceAsync(request, cancellationToken)).ToHttpResult();

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        int id,
        PlacementService placements,
        CancellationToken cancellationToken) =>
        (await placements.DeleteAsync(id, cancellationToken)).ToHttpResult();
}

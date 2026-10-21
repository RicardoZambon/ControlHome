using System.Globalization;
using ControlHome.Api.Hosting;
using ControlHome.Api.Http;
using ControlHome.Application.Floors;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ControlHome.Api.Features.Floors;

/// <summary>Floors and rooms: anyone signed in reads, Admins change.</summary>
public static class FloorEndpoints
{
    public static IEndpointRouteBuilder MapFloorEndpoints(this IEndpointRouteBuilder app)
    {
        var floors = app.MapGroup("/api/floors")
            .WithTags("Floors")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        floors.MapGet("", ListAsync)
            .WithName("listFloors")
            .WithSummary("All floors, by level.")
            .RequireAuthorization(AuthorizationPolicies.Viewer);

        floors.MapGet("/{id:int}", GetAsync)
            .WithName("getFloor")
            .WithSummary("A floor with its rooms.")
            .RequireAuthorization(AuthorizationPolicies.Viewer)
            .ProducesProblem(StatusCodes.Status404NotFound);

        floors.MapPost("", CreateAsync)
            .WithName("createFloor")
            .WithSummary("Creates an empty floor (default size 2000 × 1500 cm, grid 50 cm).")
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .WithValidation<CreateFloorRequest>();

        floors.MapPut("/{id:int}", UpdateAsync)
            .WithName("updateFloor")
            .WithSummary("Changes name, level, size, grid and background.")
            .WithDescription(
                "409 floors.concurrency when rowVersion is not the floor's current one (reload it). 400 validation.failed, " +
                "also when the new size would cut a room or a placed device (errors.widthCm / errors.heightCm = " +
                "geometry.out_of_bounds) or the background file does not exist (errors['background.fileId'] = files.not_found).")
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .WithValidation<UpdateFloorRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        floors.MapDelete("/{id:int}", DeleteAsync)
            .WithName("deleteFloor")
            .WithSummary("Deletes the floor and its rooms.")
            .WithDescription("404 floors.not_found; 409 floors.not_empty while devices are placed on it (take them off the plan first).")
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        floors.MapPut("/{id:int}/layout", SaveLayoutAsync)
            .WithName("saveFloorLayout")
            .WithSummary("Replaces the floor's rooms atomically (rooms not listed are deleted).")
            .WithDescription(
                "Placed devices whose point ends up in another room (or in none) move to that room. " +
                "409 floors.concurrency when rowVersion is not the floor's current one (reload it). 400 validation.failed " +
                "with field errors; geometry problems are codes under rooms[i].polygon: geometry.too_few_points, " +
                "geometry.too_many_points, geometry.out_of_bounds, geometry.zero_area, geometry.self_intersecting; " +
                "rooms[i].id = rooms.not_found for a room that is not on this floor.")
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .WithValidation<SaveFloorLayoutRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapGroup("/api/rooms")
            .WithTags("Rooms")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .MapGet("", ListRoomsAsync)
            .WithName("listRooms")
            .WithSummary("All rooms of all floors, by floor level and room order.")
            .RequireAuthorization(AuthorizationPolicies.Viewer);

        return app;
    }

    private static async Task<Ok<IReadOnlyList<FloorSummaryDto>>> ListAsync(FloorService floors, CancellationToken cancellationToken) =>
        TypedResults.Ok(await floors.ListAsync(cancellationToken));

    private static async Task<Results<Ok<FloorDto>, ProblemHttpResult>> GetAsync(
        int id,
        FloorService floors,
        CancellationToken cancellationToken) =>
        (await floors.GetAsync(id, cancellationToken)).ToHttpResult();

    private static async Task<Results<Created<FloorDto>, ProblemHttpResult>> CreateAsync(
        CreateFloorRequest request,
        FloorService floors,
        CancellationToken cancellationToken)
    {
        var result = await floors.CreateAsync(request, cancellationToken);
        if (result.IsFailure)
        {
            return Problems.From(result.Error);
        }

        return TypedResults.Created(
            string.Create(CultureInfo.InvariantCulture, $"/api/floors/{result.Value.Id}"),
            result.Value);
    }

    private static async Task<Results<Ok<FloorDto>, ProblemHttpResult>> UpdateAsync(
        int id,
        UpdateFloorRequest request,
        FloorService floors,
        CancellationToken cancellationToken) =>
        (await floors.UpdateAsync(id, request, cancellationToken)).ToHttpResult();

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        int id,
        FloorService floors,
        CancellationToken cancellationToken) =>
        (await floors.DeleteAsync(id, cancellationToken)).ToHttpResult();

    private static async Task<Results<Ok<FloorDto>, ProblemHttpResult>> SaveLayoutAsync(
        int id,
        SaveFloorLayoutRequest request,
        FloorService floors,
        CancellationToken cancellationToken) =>
        (await floors.SaveLayoutAsync(id, request, cancellationToken)).ToHttpResult();

    private static async Task<Ok<IReadOnlyList<RoomListItemDto>>> ListRoomsAsync(FloorService floors, CancellationToken cancellationToken) =>
        TypedResults.Ok(await floors.ListRoomsAsync(cancellationToken));
}

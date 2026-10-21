using System.ComponentModel;
using ControlHome.Api.Hosting;
using ControlHome.Api.Http;
using ControlHome.Application.History;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace ControlHome.Api.Features.History;

/// <summary>Attribute history (raw changes, 5-minute and hourly statistics) and hourly energy: anyone signed in reads.</summary>
public static class HistoryEndpoints
{
    public static IEndpointRouteBuilder MapHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/attributes")
            .WithTags("History")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .MapGet("/{id:int}/history", GetHistoryAsync)
            .WithName("getAttributeHistory")
            .WithSummary("An attribute's history over a range, oldest point first (at most 5 000 points).")
            .WithDescription(
                "resolution=auto (default) serves raw changes for ranges up to 6 h, 5-minute statistics up to 7 days and hourly " +
                "statistics beyond (the finest that fits in 5 000 points); attributes without statistics (states such as on/off " +
                "or modes) always get raw changes. Raw series start with the value in effect at `from`. Statistics trail real " +
                "time by the rollup (about a minute after a bucket ends). 400 history.invalid_resolution, history.invalid_range " +
                "(from not before to) or history.range_too_large (over 366 days); 404 attributes.not_found.")
            .RequireAuthorization(AuthorizationPolicies.Viewer)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapGroup("/api/energy")
            .WithTags("History")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .MapGet("/hourly", ListEnergyHourlyAsync)
            .WithName("listEnergyHourly")
            .WithSummary("Energy used per endpoint and hour, by hour then endpoint.")
            .WithDescription(
                "Hours overlapping [from, to) (default: the last 24 h). Each endpoint has one source: its energy meter " +
                "(counter) or, without one, its power readings integrated (integratedPower, estimated). 400 " +
                "history.invalid_range or history.range_too_large; 404 endpoints.not_found.")
            .RequireAuthorization(AuthorizationPolicies.Viewer)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<Results<Ok<HistorySeriesDto>, ProblemHttpResult>> GetHistoryAsync(
        int id,
        [FromQuery, Description("Start (inclusive, ISO 8601); default `to` minus 24 h.")] DateTimeOffset? from,
        [FromQuery, Description("End (exclusive, ISO 8601); default now.")] DateTimeOffset? to,
        [FromQuery, Description("auto (default), raw, 5m or 1h.")] string? resolution,
        HistoryQueryService history,
        CancellationToken cancellationToken) =>
        (await history.GetSeriesAsync(id, new HistoryQuery(from, to, resolution), cancellationToken)).ToHttpResult();

    private static async Task<Results<Ok<IReadOnlyList<EnergyHourlyDto>>, ProblemHttpResult>> ListEnergyHourlyAsync(
        [FromQuery, Description("Start (ISO 8601); default `to` minus 24 h.")] DateTimeOffset? from,
        [FromQuery, Description("End (exclusive, ISO 8601); default now.")] DateTimeOffset? to,
        [FromQuery, Description("One endpoint only.")] Guid? endpointId,
        HistoryQueryService history,
        CancellationToken cancellationToken) =>
        (await history.GetEnergyHourlyAsync(new EnergyQuery(from, to, endpointId), cancellationToken)).ToHttpResult();
}

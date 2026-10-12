using System.Security.Claims;
using ControlHome.Api.Hosting;
using ControlHome.Api.Http;
using ControlHome.Application.Devices;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ControlHome.Api.Features.Devices;

/// <summary>
/// Devices, endpoints, current state and commands: anyone signed in reads; Admins change device and endpoint settings;
/// Members (and Admins) favorite endpoints and send commands.
/// </summary>
public static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var devices = app.MapGroup("/api/devices")
            .WithTags("Devices")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        devices.MapGet("", ListAsync)
            .WithName("listDevices")
            .WithSummary("Devices with their endpoints, capabilities, attribute ids and current values, by name.")
            .WithDescription("Filters (all optional, combined): roomId, integrationId, capability (e.g. switch), q (name, manufacturer or model).")
            .RequireAuthorization(AuthorizationPolicies.Viewer);

        devices.MapGet("/{id:guid}", GetAsync)
            .WithName("getDevice")
            .WithSummary("A device with its vendor details, endpoints and child devices.")
            .RequireAuthorization(AuthorizationPolicies.Viewer)
            .ProducesProblem(StatusCodes.Status404NotFound);

        devices.MapPut("/{id:guid}", UpdateDeviceAsync)
            .WithName("updateDevice")
            .WithSummary("Renames a device and sets its icon and room.")
            .WithDescription("400 validation.failed (errors.roomId = rooms.not_found for an unknown room); 404 devices.not_found.")
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .WithValidation<UpdateDeviceRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        var endpoints = app.MapGroup("/api/endpoints")
            .WithTags("Devices")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapPut("/{id:guid}", UpdateEndpointAsync)
            .WithName("updateEndpoint")
            .WithSummary("Changes an endpoint's name, icon, room and hidden flag.")
            .WithDescription("400 validation.failed (errors.roomId = rooms.not_found for an unknown room); 404 endpoints.not_found.")
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .WithValidation<UpdateEndpointRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapPut("/{id:guid}/favorite", SetFavoriteAsync)
            .WithName("setEndpointFavorite")
            .WithSummary("Adds the endpoint to the dashboard's favorites, or removes it.")
            .RequireAuthorization(AuthorizationPolicies.Member)
            .WithValidation<SetFavoriteRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapPost("/{id:guid}/commands", SendCommandAsync)
            .WithName("sendCommand")
            .WithSummary("Sends a command to an endpoint; progress arrives as hub CommandStatus messages.")
            .WithDescription(
                "202 with the command id. 400 commands.unsupported (no such capability or command on this endpoint) or " +
                "commands.invalid_arguments (errors['arguments.<key>'] = commands.argument_*); 403 safety.step_up_required " +
                "(safety-critical: POST /api/auth/step-up first, then resend with the same idempotencyKey) or " +
                "safety.not_allowed; 404 endpoints.not_found; 409 commands.duplicate (idempotency key already used) or " +
                "commands.gate_closed; 423 safety.locked (retryAfterSeconds); 503 commands.unavailable (integration not running).")
            .RequireAuthorization(AuthorizationPolicies.Member)
            .WithValidation<CommandRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status423Locked)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        app.MapGroup("/api/state")
            .WithTags("Devices")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .MapGet("", GetStateAsync)
            .WithName("getState")
            .WithSummary("Every attribute's current value (resync after a hub reconnect).")
            .RequireAuthorization(AuthorizationPolicies.Viewer);

        return app;
    }

    private static async Task<Ok<IReadOnlyList<DeviceSummaryDto>>> ListAsync(
        [AsParameters] DeviceListQuery query,
        DeviceQueryService devices,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await devices.ListAsync(query, cancellationToken));

    private static async Task<Results<Ok<DeviceDetailDto>, ProblemHttpResult>> GetAsync(
        Guid id,
        DeviceQueryService devices,
        CancellationToken cancellationToken) =>
        (await devices.GetAsync(id, cancellationToken)).ToHttpResult();

    private static async Task<Results<Ok<DeviceDetailDto>, ProblemHttpResult>> UpdateDeviceAsync(
        Guid id,
        UpdateDeviceRequest request,
        DeviceAdminService devices,
        CancellationToken cancellationToken) =>
        (await devices.UpdateDeviceAsync(id, request, cancellationToken)).ToHttpResult();

    private static async Task<Results<Ok<EndpointDto>, ProblemHttpResult>> UpdateEndpointAsync(
        Guid id,
        UpdateEndpointRequest request,
        DeviceAdminService devices,
        CancellationToken cancellationToken) =>
        (await devices.UpdateEndpointAsync(id, request, cancellationToken)).ToHttpResult();

    private static async Task<Results<Ok<EndpointDto>, ProblemHttpResult>> SetFavoriteAsync(
        Guid id,
        SetFavoriteRequest request,
        DeviceAdminService devices,
        CancellationToken cancellationToken) =>
        (await devices.SetFavoriteAsync(id, request, cancellationToken)).ToHttpResult();

    private static async Task<Results<Accepted<CommandAcceptedDto>, ProblemHttpResult>> SendCommandAsync(
        Guid id,
        CommandRequest request,
        ClaimsPrincipal user,
        DeviceCommandService commands,
        CancellationToken cancellationToken)
    {
        var result = await commands.ExecuteAsync(id, request, CommandPrincipals.FromUser(user), cancellationToken);
        if (result.IsFailure)
        {
            return Problems.From(result.Error);
        }

        return TypedResults.Accepted((string?)null, result.Value);
    }

    private static async Task<Ok<IReadOnlyList<AttributeStateDto>>> GetStateAsync(
        DeviceQueryService devices,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await devices.GetStatesAsync(cancellationToken));
}

using System.Globalization;
using ControlHome.Api.Hosting;
using ControlHome.Api.Http;
using ControlHome.Application.Devices;
using ControlHome.Application.Integrations;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ControlHome.Api.Features.Integrations;

/// <summary>
/// The connector catalogue and integrations (milestone 1.2: created directly from a connector's config fields, no setup
/// flow yet). Anyone signed in reads; Admins create and remove.
/// </summary>
public static class IntegrationEndpoints
{
    public static IEndpointRouteBuilder MapIntegrationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/connectors")
            .WithTags("Integrations")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .MapGet("", ListConnectors)
            .WithName("listConnectors")
            .WithSummary("The connectors of this build (descriptors: names, icon, config forms, features).")
            .RequireAuthorization(AuthorizationPolicies.Viewer);

        var integrations = app.MapGroup("/api/integrations")
            .WithTags("Integrations")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        integrations.MapGet("", ListAsync)
            .WithName("listIntegrations")
            .WithSummary("All integrations with their status and device count, by name.")
            .RequireAuthorization(AuthorizationPolicies.Viewer);

        integrations.MapPost("", CreateAsync)
            .WithName("createIntegration")
            .WithSummary("Creates an integration from a connector key and its config; its runtime starts right away.")
            .WithDescription(
                "400 validation.failed (errors.connectorKey = connectors.not_found for an unknown connector) or " +
                "integrations.invalid_config (errors['config.<key>'] = commands.argument_* codes); 409 runtime.standby (this " +
                "instance is a read-only standby: make the change on the active one); 503 runtime.starting (this instance " +
                "is still deciding whether it runs the devices: try again in a moment). Secret config fields are stored " +
                "encrypted and never returned.")
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .WithValidation<CreateIntegrationRequest>()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        integrations.MapDelete("/{id:guid}", DeleteAsync)
            .WithName("deleteIntegration")
            .WithSummary("Stops the integration and deletes it with all its devices and their state.")
            .WithDescription(
                "404 integrations.not_found; 409 runtime.standby (this instance is a read-only standby); 503 " +
                "runtime.starting (still deciding whether it runs the devices).")
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    private static Ok<IReadOnlyList<ConnectorDto>> ListConnectors(ConnectorCatalog connectors) => TypedResults.Ok(connectors.ToDtos());

    private static async Task<Ok<IReadOnlyList<IntegrationDto>>> ListAsync(IntegrationService integrations, CancellationToken cancellationToken) =>
        TypedResults.Ok(await integrations.ListAsync(cancellationToken));

    private static async Task<Results<Created<IntegrationDto>, ProblemHttpResult>> CreateAsync(
        CreateIntegrationRequest request,
        IntegrationService integrations,
        CancellationToken cancellationToken)
    {
        var result = await integrations.CreateAsync(request, cancellationToken);
        if (result.IsFailure)
        {
            return Problems.From(result.Error);
        }

        return TypedResults.Created(
            string.Create(CultureInfo.InvariantCulture, $"/api/integrations/{result.Value.Id}"),
            result.Value);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id,
        IntegrationService integrations,
        CancellationToken cancellationToken) =>
        (await integrations.DeleteAsync(id, cancellationToken)).ToHttpResult();
}

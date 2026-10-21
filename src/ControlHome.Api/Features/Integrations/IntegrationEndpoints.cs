using System.Globalization;
using ControlHome.Api.Hosting;
using ControlHome.Api.Http;
using ControlHome.Application.Devices;
using ControlHome.Application.Integrations;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ControlHome.Api.Features.Integrations;

/// <summary>
/// The connector catalogue, integrations and their setup / reauth flows. Anyone signed in reads; Admins create (from config
/// fields, or through a flow), change and remove.
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

        integrations.MapPut("/{id:guid}", UpdateAsync)
            .WithName("updateIntegration")
            .WithSummary("Renames an integration and enables or disables it.")
            .WithDescription(
                "Disabling stops its runtime (status Stopped; devices are kept, availability Unknown); enabling starts it. " +
                "404 integrations.not_found; 409 runtime.standby; 503 runtime.starting.")
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .WithValidation<UpdateIntegrationRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound)
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

        integrations.MapPost("/flows", StartFlowAsync)
            .WithName("startIntegrationFlow")
            .WithSummary("Starts a connector's setup flow and returns its first step.")
            .WithDescription(
                "Answer each step with POST /api/integrations/flows/{flowId} until a done (the integration was created: " +
                "integrationId) or abort step. Connectors without a flow of their own get one form with their config " +
                "fields. Flows are kept in memory for 15 minutes after their last step and belong to the admin who " +
                "started them. 400 validation.failed (errors.connectorKey = connectors.not_found); 409 runtime.standby; " +
                "503 runtime.starting.")
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .WithValidation<StartFlowRequest>()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        integrations.MapPost("/flows/{flowId:guid}", ContinueFlowAsync)
            .WithName("continueIntegrationFlow")
            .WithSummary("Answers the flow's current step and returns the next one.")
            .WithDescription(
                "input: a form's values (secret fields are write-only), {\"choice\": key}, a pasted value, or {} to poll a " +
                "progress step. A done step created (setup) or updated (reauth) the integration with its secrets " +
                "encrypted and started its runtime. 404 flows.not_found (finished, cancelled, expired, or another " +
                "admin's); 409 runtime.standby; 503 runtime.starting.")
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .WithValidation<ContinueFlowRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        integrations.MapDelete("/flows/{flowId:guid}", CancelFlowAsync)
            .WithName("cancelIntegrationFlow")
            .WithSummary("Abandons a flow (nothing is created or changed).")
            .WithDescription("404 flows.not_found.")
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .ProducesProblem(StatusCodes.Status404NotFound);

        integrations.MapPost("/{id:guid}/flows/reauth", StartReauthFlowAsync)
            .WithName("startIntegrationReauthFlow")
            .WithSummary("Starts the connector's flow for an existing integration: re-authorize it or change its settings.")
            .WithDescription(
                "Its devices, names, rooms and history are kept; the done step updates the configuration and replaces the " +
                "secrets, then restarts the integration. 404 integrations.not_found or connectors.not_found (the " +
                "connector is not in this version); 409 runtime.standby; 503 runtime.starting.")
            .RequireAuthorization(AuthorizationPolicies.Admin)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    private static async Task<Results<Ok<IntegrationDto>, ProblemHttpResult>> UpdateAsync(
        Guid id,
        UpdateIntegrationRequest request,
        IntegrationService integrations,
        CancellationToken cancellationToken) =>
        (await integrations.UpdateAsync(id, request, cancellationToken)).ToHttpResult();

    private static async Task<Results<Ok<FlowStepDto>, ProblemHttpResult>> StartFlowAsync(
        StartFlowRequest request,
        IntegrationFlowService flows,
        CancellationToken cancellationToken) =>
        (await flows.StartAsync(request, cancellationToken)).ToHttpResult();

    private static async Task<Results<Ok<FlowStepDto>, ProblemHttpResult>> ContinueFlowAsync(
        Guid flowId,
        ContinueFlowRequest request,
        IntegrationFlowService flows,
        CancellationToken cancellationToken) =>
        (await flows.ContinueAsync(flowId, request, cancellationToken)).ToHttpResult();

    private static async Task<Results<NoContent, ProblemHttpResult>> CancelFlowAsync(Guid flowId, IntegrationFlowService flows) =>
        (await flows.CancelAsync(flowId)).ToHttpResult();

    private static async Task<Results<Ok<FlowStepDto>, ProblemHttpResult>> StartReauthFlowAsync(
        Guid id,
        IntegrationFlowService flows,
        CancellationToken cancellationToken) =>
        (await flows.StartReauthAsync(id, cancellationToken)).ToHttpResult();

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

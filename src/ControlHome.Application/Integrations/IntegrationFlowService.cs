using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Auditing;
using ControlHome.Application.Common;
using ControlHome.Application.Devices;
using ControlHome.Application.Runtime;
using ControlHome.Application.Security;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ControlHome.Application.Integrations;

/// <summary>
/// Runs connector flows for the integrations UI: setup (<see cref="StartAsync"/>), reauth / reconfiguration of an existing
/// integration (<see cref="StartReauthAsync"/>), one answer at a time (<see cref="ContinueAsync"/>). A connector without a
/// flow of its own gets a single form with its config fields. Flows live in memory (<see cref="IntegrationFlowStore"/>, 15
/// minutes after their last step) and belong to the admin who started them. A <see cref="DoneStep"/> creates the
/// integration — or updates the one being re-authorized, keeping its devices — with the secrets encrypted, and starts its
/// runtime. Like every integration change, flows are refused on a standby (409 <c>runtime.standby</c>) and while the
/// runtime is starting (503 <c>runtime.starting</c>).
/// </summary>
public sealed partial class IntegrationFlowService(
    IControlHomeDb db,
    ConnectorCatalog connectors,
    IntegrationFlowStore flows,
    IIntegrationSecretProtector secretProtector,
    IIntegrationSecretStores secretStores,
    IIntegrationRuntimeControl runtime,
    IRuntimeStatus runtimeStatus,
    IRuntimeClock clock,
    ICurrentActor actor,
    IAuditLog auditLog,
    KnownSecrets knownSecrets,
    TimeProvider timeProvider,
    ILogger<IntegrationFlowService> logger)
{
    /// <summary>
    /// Starts a connector's setup flow. Errors: 400 <c>validation.failed</c> with <c>errors.connectorKey =
    /// [connectors.not_found]</c>; 409 <c>runtime.standby</c>; 503 <c>runtime.starting</c>.
    /// </summary>
    public async Task<Result<FlowStepDto>> StartAsync(StartFlowRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (runtimeStatus.IntegrationChangeRefusal() is { } refused)
        {
            return refused;
        }

        if (connectors.Find(request.ConnectorKey) is not { } factory)
        {
            return AppError.FieldErrors(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["connectorKey"] = [ErrorCodes.ConnectorsNotFound],
            });
        }

        return await BeginAsync(factory, existing: null, cancellationToken);
    }

    /// <summary>
    /// Starts the connector's flow for an existing integration (re-authorize, change settings; its devices are kept).
    /// Errors: 404 <c>integrations.not_found</c>, 404 <c>connectors.not_found</c> (its connector is not in this build),
    /// 409 <c>runtime.standby</c>, 503 <c>runtime.starting</c>.
    /// </summary>
    public async Task<Result<FlowStepDto>> StartReauthAsync(Guid integrationId, CancellationToken cancellationToken)
    {
        var integration = await db.Integrations.AsNoTracking().SingleOrDefaultAsync(i => i.Id == integrationId, cancellationToken);
        if (integration is null)
        {
            return AppError.NotFound(ErrorCodes.IntegrationsNotFound, "No such integration.");
        }

        if (runtimeStatus.IntegrationChangeRefusal() is { } refused)
        {
            return refused;
        }

        if (connectors.Find(integration.ConnectorKey) is not { } factory)
        {
            return AppError.NotFound(ErrorCodes.ConnectorsNotFound, "This integration's connector is not available in this version.");
        }

        using var config = JsonDocument.Parse(integration.ConfigJson);
        var existing = new IntegrationInstance(integration.Id, integration.Name, config.RootElement.Clone(), secretStores.Open(integration.Id));
        return await BeginAsync(factory, existing, cancellationToken);
    }

    /// <summary>
    /// Answers the flow's current step and returns the next one. Errors: 404 <c>flows.not_found</c> (unknown, finished,
    /// expired or someone else's), 409 <c>runtime.standby</c>, 503 <c>runtime.starting</c> (the flow is kept: try again).
    /// </summary>
    public async Task<Result<FlowStepDto>> ContinueAsync(Guid flowId, ContinueFlowRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (flows.Find(flowId, OwnerId) is not { } session)
        {
            return FlowNotFound();
        }

        if (runtimeStatus.IntegrationChangeRefusal() is { } refused)
        {
            return refused;
        }

        var input = request.Input is { ValueKind: JsonValueKind.Object } given ? given : JsonSerializer.SerializeToElement(new JsonObject());
        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            // Re-checked under the gate: an answer that queued behind the one finishing the flow finds it gone.
            if (flows.Find(flowId, OwnerId) is null || session.CurrentStepId is not { } stepId)
            {
                return FlowNotFound();
            }

            // Typed secrets are masked in logs from now on, whatever the connector does with them.
            foreach (var field in session.SecretFields)
            {
                if (input.TryGetProperty(field, out var secret) && secret.ValueKind == JsonValueKind.String)
                {
                    knownSecrets.Register(secret.GetString());
                }
            }

            return await RunAsync(session, () => session.Flow.ContinueAsync(stepId, input, cancellationToken), cancellationToken);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>Abandons a flow (the wizard was closed); 404 <c>flows.not_found</c>.</summary>
    public async Task<Result> CancelAsync(Guid flowId)
    {
        if (flows.Find(flowId, OwnerId) is null)
        {
            return FlowNotFound();
        }

        await flows.RemoveAsync(flowId);
        return Result.Success();
    }

    private Guid OwnerId => actor.UserId ?? Guid.Empty;

    private static AppError FlowNotFound() =>
        AppError.NotFound(ErrorCodes.FlowsNotFound, "This setup flow does not exist (any more). Start again.");

    private async Task<Result<FlowStepDto>> BeginAsync(IConnectorFactory factory, IntegrationInstance? existing, CancellationToken cancellationToken)
    {
        var flow = factory.CreateSetupFlow() ?? new ConfigFieldsFlow(factory.Descriptor);
        var session = new FlowSession(Guid.CreateVersion7(), factory.Descriptor.Key, existing?.Id, OwnerId, flow);
        await flows.AddAsync(session);
        var context = new FlowContext(clock.Time, logger, existing);
        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            return await RunAsync(session, () => flow.StartAsync(context, cancellationToken), cancellationToken);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>Runs one step of the connector's flow and handles what it returns.</summary>
    private async Task<Result<FlowStepDto>> RunAsync(FlowSession session, Func<Task<FlowStep>> next, CancellationToken cancellationToken)
    {
        FlowStep step;
        try
        {
            step = await next();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never the exception object: a connector's message may quote a secret.
            LogFlowFailed(session.ConnectorKey, ex.GetType().Name, knownSecrets.Redact(ex.Message));
            step = new AbortStep(
                "failed",
                new LocalizedText("The connector failed. Try again; the server log has the details.", "O conector falhou. Tente novamente; o log do servidor tem os detalhes."));
        }

        switch (step)
        {
            case DoneStep done:
                var applied = await ApplyAsync(session, done, cancellationToken);
                await flows.RemoveAsync(session.Id);
                return applied;
            case AbortStep abort:
                await flows.RemoveAsync(session.Id);
                return ToDto(session.Id, abort, null);
            case ExternalUrlStep { Url: var url } when !url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps:
                LogInvalidStep(session.ConnectorKey);
                await flows.RemoveAsync(session.Id);
                return ToDto(session.Id, InvalidStep(), null);
            default:
                session.CurrentStepId = StepIdOf(step);
                session.SecretFields = step switch
                {
                    FormStep form => [.. form.Fields.Where(f => f.Secret).Select(f => f.Key)],
                    ExternalUrlStep { PasteBack: { Secret: true } pasteBack } => [pasteBack.Key],
                    _ => [],
                };
                flows.Touch(session);
                return ToDto(session.Id, step, null);
        }
    }

    /// <summary>Creates the integration, or updates the one being re-authorized; an unusable result aborts the flow.</summary>
    private async Task<Result<FlowStepDto>> ApplyAsync(FlowSession session, DoneStep done, CancellationToken cancellationToken)
    {
        if (connectors.Find(session.ConnectorKey) is not { } factory)
        {
            return ToDto(session.Id, InvalidStep(), null);
        }

        var config = done.Config.ValueKind == JsonValueKind.Object ? done.Config : JsonSerializer.SerializeToElement(new JsonObject());
        var id = session.IntegrationId ?? Guid.CreateVersion7();
        try
        {
            // The connector parses its own config: refuse what it would refuse at start-up.
            await using var probe = factory.CreateRuntime(new IntegrationInstance(id, done.Name, config, EmptySecretStore.Instance));
        }
        catch (ArgumentException)
        {
            LogInvalidStep(session.ConnectorKey);
            return ToDto(session.Id, InvalidStep(), null);
        }

        var secretFields = done.Secrets.Keys.Order(StringComparer.Ordinal).ToArray();
        var now = timeProvider.GetUtcNow();
        if (session.IntegrationId is null)
        {
            var integration = new Integration
            {
                Id = id,
                ConnectorKey = factory.Descriptor.Key,
                Name = NameOf(done.Name, factory.Descriptor.DisplayName.En),
                ConfigJson = config.GetRawText(),
                SecretsProtected = secretProtector.Protect(done.Secrets),
                Status = IntegrationStatus.Connecting,
                Enabled = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            db.Integrations.Add(integration);
            await db.SaveChangesAsync(cancellationToken);
            await auditLog.WriteAsync(
                new AuditEvent(
                    AuditActions.IntegrationCreated,
                    IntegrationService.IntegrationTarget,
                    IdOf(id),
                    new { integration.ConnectorKey, integration.Name, SecretFields = secretFields, Flow = true }),
                cancellationToken);
            await runtime.StartAsync(id, cancellationToken);
            return ToDto(session.Id, done, id);
        }

        var existing = await db.Integrations.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (existing is null)
        {
            return ToDto(
                session.Id,
                new AbortStep("integration_deleted", new LocalizedText("The integration was deleted meanwhile.", "A integração foi excluída nesse meio-tempo.")),
                null);
        }

        // Stopped first: the old runtime must not save a rotated token over the new secrets.
        await runtime.StopAsync(id, cancellationToken);
        existing.ConfigJson = config.GetRawText();
        existing.SecretsProtected = secretProtector.ReplaceValues(existing.SecretsProtected, done.Secrets);
        existing.Status = existing.Enabled ? IntegrationStatus.Connecting : IntegrationStatus.Stopped;
        existing.StatusMessage = null;
        existing.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        await auditLog.WriteAsync(
            new AuditEvent(
                AuditActions.IntegrationUpdated,
                IntegrationService.IntegrationTarget,
                IdOf(id),
                new { existing.ConnectorKey, Fields = new[] { "config", "secrets" }, SecretFields = secretFields, Flow = true }),
            cancellationToken);
        if (existing.Enabled)
        {
            await runtime.StartAsync(id, cancellationToken);
        }

        return ToDto(session.Id, done, id);
    }

    private static string NameOf(string? suggested, string fallback)
    {
        var name = string.IsNullOrWhiteSpace(suggested) ? fallback : suggested.Trim();
        return name.Length <= DeviceLimits.NameMaxLength ? name : name[..DeviceLimits.NameMaxLength];
    }

    private static AbortStep InvalidStep() =>
        new("invalid_step", new LocalizedText("The connector answered with something unusable.", "O conector respondeu com algo inutilizável."));

    private static string? StepIdOf(FlowStep step) => step switch
    {
        FormStep form => form.StepId,
        ExternalUrlStep external => external.StepId,
        ProgressStep progress => progress.StepId,
        ChooseStep choose => choose.StepId,
        _ => null,
    };

    /// <summary>The API shape of a step; a form's initial values never carry its secret fields.</summary>
    private static FlowStepDto ToDto(Guid flowId, FlowStep step, Guid? integrationId)
    {
        var none = new Dictionary<string, LocalizedTextDto>();
        return step switch
        {
            FormStep form => new FlowStepDto(
                flowId,
                FlowStepTypes.Form,
                form.StepId,
                T(form.Title),
                T(form.Description),
                [.. form.Fields.Select(ConnectorCatalog.ToDto)],
                WithoutSecrets(form.Values, form.Fields),
                form.Errors.ToDictionary(e => e.Key, e => T(e.Value)!, StringComparer.Ordinal),
                null,
                null,
                null,
                [],
                null,
                null,
                null),
            ExternalUrlStep external => new FlowStepDto(
                flowId,
                FlowStepTypes.ExternalUrl,
                external.StepId,
                T(external.Title),
                T(external.Description),
                [],
                null,
                none,
                external.Url.AbsoluteUri,
                external.PasteBack is null ? null : ConnectorCatalog.ToDto(external.PasteBack),
                null,
                [],
                null,
                null,
                null),
            ProgressStep progress => new FlowStepDto(
                flowId,
                FlowStepTypes.Progress,
                progress.StepId,
                T(progress.Title),
                T(progress.Description),
                [],
                null,
                none,
                null,
                null,
                progress.Timeout?.TotalSeconds,
                [],
                null,
                null,
                null),
            ChooseStep choose => new FlowStepDto(
                flowId,
                FlowStepTypes.Choose,
                choose.StepId,
                T(choose.Title),
                T(choose.Description),
                [],
                null,
                none,
                null,
                null,
                null,
                [.. choose.Choices.Select(c => new FlowChoiceDto(c.Key, T(c.Label)!, T(c.Description)))],
                null,
                null,
                null),
            DoneStep => new FlowStepDto(flowId, FlowStepTypes.Done, null, null, null, [], null, none, null, null, null, [], integrationId, null, null),
            AbortStep abort => new FlowStepDto(flowId, FlowStepTypes.Abort, null, null, null, [], null, none, null, null, null, [], null, abort.Reason, T(abort.Message)),
            _ => throw new ArgumentOutOfRangeException(nameof(step), step.GetType().Name, "Unknown flow step."),
        };
    }

    private static LocalizedTextDto? T(LocalizedText? text) => text is null ? null : ConnectorCatalog.ToDto(text);

    private static JsonElement? WithoutSecrets(JsonElement? values, IReadOnlyList<FieldSchema> fields)
    {
        if (values is not { ValueKind: JsonValueKind.Object } given)
        {
            return null;
        }

        var copy = JsonNode.Parse(given.GetRawText())!.AsObject();
        foreach (var field in fields.Where(f => f.Secret))
        {
            copy.Remove(field.Key);
        }

        return JsonSerializer.SerializeToElement(copy);
    }

    private static string IdOf(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A {ConnectorKey} flow failed ({ExceptionType}): {Reason}")]
    private partial void LogFlowFailed(string connectorKey, string exceptionType, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A {ConnectorKey} flow returned an unusable step; the flow was aborted")]
    private partial void LogInvalidStep(string connectorKey);
}

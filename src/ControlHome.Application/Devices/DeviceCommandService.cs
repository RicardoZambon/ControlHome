using System.Text.Json;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Auditing;
using ControlHome.Application.Capabilities;
using ControlHome.Application.Common;
using ControlHome.Application.Integrations;
using ControlHome.Application.Security;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Capabilities;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Application.Devices;

/// <summary>
/// The only path from a user (or, later, an automation) to a device: resolves the endpoint and capability, validates the
/// arguments against the command definition and the endpoint's constraints (with the current state for mode-dependent
/// ranges), checks the gate attribute, applies the <see cref="SafetyPolicy"/> (step-up grant per safety-critical command),
/// enforces at-most-once delivery with the idempotency key, and hands the command to <see cref="CommandTracker"/>, which
/// sends it, reports its progress over the hub and audits <c>commands.executed</c>.
/// </summary>
public sealed class DeviceCommandService(
    IControlHomeDb db,
    IAttributeStateReader states,
    ICommandDispatcher dispatcher,
    CommandTracker tracker,
    SafetyGrantStore grants,
    ConnectorCatalog connectors,
    ICurrentActor actor)
{
    private static readonly JsonElement _noArguments = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>
    /// Accepts a command (202 with its id). Errors: 404 <c>endpoints.not_found</c>; 400 <c>commands.unsupported</c>,
    /// <c>commands.invalid_arguments</c>; 403 <c>safety.not_allowed</c>, <c>safety.step_up_required</c>; 423
    /// <c>safety.locked</c>; 409 <c>commands.gate_closed</c>, <c>commands.duplicate</c>; 503 <c>commands.unavailable</c>.
    /// </summary>
    public async Task<Result<CommandAcceptedDto>> ExecuteAsync(
        Guid endpointId,
        CommandRequest request,
        CommandPrincipal principal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(principal);

        var target = await (from endpoint in db.Endpoints.AsNoTracking()
                            join device in db.Devices.AsNoTracking() on endpoint.DeviceId equals device.Id
                            join integration in db.Integrations.AsNoTracking() on device.IntegrationId equals integration.Id
                            where endpoint.Id == endpointId
                            select new
                            {
                                EndpointKey = endpoint.Key,
                                DeviceId = device.Id,
                                device.IntegrationId,
                                device.ExternalId,
                                device.DeviceClass,
                                integration.ConnectorKey,
                            })
            .SingleOrDefaultAsync(cancellationToken);
        if (target is null)
        {
            return AppError.NotFound(ErrorCodes.EndpointsNotFound, "No such device control.");
        }

        var capability = await db.EndpointCapabilities.AsNoTracking()
            .Where(c => c.EndpointId == endpointId && c.CapabilityId == request.CapabilityId && !c.Retired)
            .Select(c => new
            {
                c.Id,
                c.ConstraintsJson,
                c.GenericDefinitionJson,
                Attributes = c.Attributes.Select(a => new { a.Id, a.Key }).ToList(),
            })
            .SingleOrDefaultAsync(cancellationToken);
        var definition = capability is null ? null : DefinitionOf(request.CapabilityId, capability.GenericDefinitionJson);
        var command = definition?.FindCommand(request.Command);
        if (capability is null || command is null)
        {
            return AppError.Validation(ErrorCodes.CommandsUnsupported, "This device control has no such command.");
        }

        var decision = SafetyPolicy.Evaluate(principal, command, target.DeviceClass);
        if (decision == SafetyDecision.Forbidden)
        {
            return AppError.Forbidden(ErrorCodes.SafetyNotAllowed, "You are not allowed to send this command.");
        }

        var values = await states.GetAsync(capability.Attributes.Select(a => a.Id).ToList(), cancellationToken);
        var current = capability.Attributes
            .Where(a => values.ContainsKey(a.Id))
            .ToDictionary(a => a.Key, a => values[a.Id].Value, StringComparer.Ordinal);

        var arguments = request.Arguments is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } given
            ? given.Clone()
            : _noArguments;
        ArgumentValidationResult validation;
        try
        {
            validation = CommandValidator.Validate(command, arguments, ParseJson(capability.ConstraintsJson), current);
        }
        catch (JsonException)
        {
            // Malformed stored constraints are a connector bug: validate against the definition alone.
            validation = CommandValidator.Validate(command, arguments, null, current);
        }

        if (!validation.IsValid)
        {
            return CommandArgumentErrors.ToAppError(validation);
        }

        if (command.GateAttribute is { } gate && current.GetValueOrDefault(gate) is not { B: true })
        {
            return AppError.Conflict(ErrorCodes.CommandsGateClosed, "The device does not accept remote commands right now.");
        }

        if (!dispatcher.IsRunning(target.IntegrationId))
        {
            return AppError.Unavailable(ErrorCodes.CommandsUnavailable, "The device's integration is not running.");
        }

        var key = request.IdempotencyKey ?? Guid.NewGuid().ToString("N");
        if (tracker.IsKnownKey(key))
        {
            return Duplicate();
        }

        var safetyCritical = SafetyPolicy.IsGated(command, target.DeviceClass);
        if (decision == SafetyDecision.StepUpRequired)
        {
            var userId = principal.UserId ?? throw new InvalidOperationException("A user principal needs a user id.");
            if (grants.LockedFor(userId) is { } locked)
            {
                return StepUpService.Locked(locked);
            }

            if (!grants.TryConsume(userId))
            {
                return AppError.Forbidden(ErrorCodes.SafetyStepUpRequired, "Confirm with your safety PIN first.");
            }
        }

        if (!tracker.TryReserveKey(key))
        {
            return Duplicate();
        }

        var cloud = connectors.Find(target.ConnectorKey)?.Descriptor.Features.Cloud == true;
        var commandId = Guid.CreateVersion7();
        tracker.Submit(new TrackedCommand(
            commandId,
            target.IntegrationId,
            endpointId,
            request.CapabilityId,
            command,
            capability.Attributes.Select(a => a.Id).ToList(),
            new DeviceCommandBatch(
                target.ExternalId,
                [new DeviceCommand(target.EndpointKey, request.CapabilityId, command.Name, arguments)],
                key),
            command.Timeout ?? (cloud ? CommandTracker.DefaultCloudTimeout : CommandTracker.DefaultLocalTimeout),
            CommandTracker.IsCoalescable(command, safetyCritical),
            AuditActorSnapshot.From(actor),
            AuditArguments: !safetyCritical));
        return new CommandAcceptedDto(commandId);
    }

    private static AppError Duplicate() =>
        AppError.Conflict(ErrorCodes.CommandsDuplicate, "This command was already sent (same idempotency key).");

    private static CapabilityDefinition? DefinitionOf(string capabilityId, string? genericDefinitionJson)
    {
        if (!string.Equals(capabilityId, CapabilityIds.Generic, StringComparison.Ordinal))
        {
            return CapabilityCatalog.Find(capabilityId);
        }

        try
        {
            return string.IsNullOrWhiteSpace(genericDefinitionJson)
                ? null
                : CapabilityJson.Deserialize<CapabilityDefinition>(genericDefinitionJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Auditing;
using ControlHome.Application.Capabilities;
using ControlHome.Application.Common;
using ControlHome.Application.Devices;
using ControlHome.Application.Realtime;
using ControlHome.Application.Runtime;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Devices;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Application.Integrations;

/// <summary>Encrypts an integration's secret configuration values (implemented with Data Protection by Infrastructure).</summary>
public interface IIntegrationSecretProtector
{
    /// <summary>The secrets encrypted for <c>Integration.SecretsProtected</c>, or <c>null</c> when there are none.</summary>
    string? Protect(IReadOnlyDictionary<string, string> secrets);
}

public sealed class CreateIntegrationRequestValidator : AbstractValidator<CreateIntegrationRequest>
{
    public CreateIntegrationRequestValidator()
    {
        RuleFor(x => x.ConnectorKey).NotEmpty().MaximumLength(DeviceLimits.ConnectorKeyMaxLength);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(DeviceLimits.NameMaxLength).Must(n => !string.IsNullOrWhiteSpace(n))
            .WithMessage("'{PropertyName}' must not be blank.");
    }
}

/// <summary>
/// Integrations without a setup flow (milestone 1.2): created from a connector key and its config fields, removed with
/// their devices. Secret config fields are split off and stored encrypted (<see cref="IIntegrationSecretProtector"/>);
/// the runtime starts right away (when this process is the active runtime). A read-only standby (another instance on the
/// same database runs the devices) refuses both with 409 <c>runtime.standby</c>: the active instance would never apply them;
/// so does a process still deciding whether it is active (503 <c>runtime.starting</c>).
/// </summary>
public sealed class IntegrationService(
    IControlHomeDb db,
    ConnectorCatalog connectors,
    IIntegrationSecretProtector secretProtector,
    IIntegrationRuntimeControl runtime,
    IRuntimeStatus runtimeStatus,
    DeviceQueryService queries,
    IRealtimePublisher publisher,
    IAuditLog auditLog,
    TimeProvider timeProvider)
{
    public const string IntegrationTarget = "integration";
    public const string ConfigPath = "config";

    public Task<IReadOnlyList<IntegrationDto>> ListAsync(CancellationToken cancellationToken) =>
        queries.ListIntegrationsAsync(cancellationToken);

    /// <summary>
    /// Errors: 400 <c>validation.failed</c> with <c>errors.connectorKey = [connectors.not_found]</c>; 400
    /// <c>integrations.invalid_config</c> with <c>errors["config.&lt;key&gt;"]</c> (<c>commands.argument_*</c> codes); 409
    /// <c>runtime.standby</c>; 503 <c>runtime.starting</c>.
    /// </summary>
    public async Task<Result<IntegrationDto>> CreateAsync(CreateIntegrationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (NotActiveError() is { } notActive)
        {
            return notActive;
        }

        if (connectors.Find(request.ConnectorKey) is not { } factory)
        {
            return AppError.FieldErrors(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["connectorKey"] = [ErrorCodes.ConnectorsNotFound],
            });
        }

        var config = request.Config is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } given
            ? given
            : JsonSerializer.SerializeToElement(new JsonObject());
        var fields = factory.Descriptor.ConfigFields;
        var validation = FieldSchemaValidator.Validate(fields, config);
        if (!validation.IsValid)
        {
            return InvalidConfig(validation);
        }

        var (plain, secrets) = Split(fields, config);
        var now = timeProvider.GetUtcNow();
        var integration = new Integration
        {
            Id = Guid.CreateVersion7(),
            ConnectorKey = factory.Descriptor.Key,
            Name = request.Name.Trim(),
            ConfigJson = plain.ToJsonString(),
            SecretsProtected = secretProtector.Protect(secrets),
            Status = IntegrationStatus.Connecting,
            Enabled = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        try
        {
            // The connector parses its own config: refuse what it would refuse at start-up.
            await using var probe = factory.CreateRuntime(
                new IntegrationInstance(integration.Id, integration.Name, JsonSerializer.SerializeToElement(plain), EmptySecretStore.Instance));
        }
        catch (ArgumentException)
        {
            return AppError.Validation(ErrorCodes.IntegrationsInvalidConfig, "The connector rejected the configuration.");
        }

        db.Integrations.Add(integration);
        await db.SaveChangesAsync(cancellationToken);
        await auditLog.WriteAsync(
            new AuditEvent(
                AuditActions.IntegrationCreated,
                IntegrationTarget,
                IdOf(integration.Id),
                new { integration.ConnectorKey, integration.Name, SecretFields = secrets.Keys.Order(StringComparer.Ordinal).ToArray() }),
            cancellationToken);

        await runtime.StartAsync(integration.Id, cancellationToken);
        return new IntegrationDto(
            integration.Id,
            integration.ConnectorKey,
            integration.Name,
            integration.Status.ToString(),
            integration.StatusMessage,
            integration.Enabled,
            0,
            integration.CreatedAtUtc,
            integration.UpdatedAtUtc);
    }

    /// <summary>
    /// Stops the runtime and deletes the integration with all its devices (and their state); 404 <c>integrations.not_found</c>,
    /// 409 <c>runtime.standby</c>, 503 <c>runtime.starting</c>.
    /// </summary>
    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var integration = await db.Integrations.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (integration is null)
        {
            return AppError.NotFound(ErrorCodes.IntegrationsNotFound, "No such integration.");
        }

        if (NotActiveError() is { } notActive)
        {
            return notActive;
        }

        await runtime.StopAsync(id, cancellationToken);

        // Parent links do not cascade in the database: clear them within the integration first.
        await db.Devices
            .Where(d => d.IntegrationId == id && d.ParentDeviceId != null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ParentDeviceId, (Guid?)null), cancellationToken);
        db.Integrations.Remove(integration);
        await db.SaveChangesAsync(cancellationToken);

        await runtime.RemovedAsync(id, cancellationToken);
        await auditLog.WriteAsync(
            new AuditEvent(AuditActions.IntegrationDeleted, IntegrationTarget, IdOf(id), new { integration.ConnectorKey, integration.Name }),
            cancellationToken);
        await publisher.DevicesChangedAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>
    /// Integration changes are made only where they are applied. The active instance reads integrations only when it becomes
    /// active, so a change made on a standby would never reach it (a deleted integration would keep running with its
    /// credentials): 409 <c>runtime.standby</c>. While this process is still deciding (Starting, with a device runtime) it
    /// may yet become a standby: 503 <c>runtime.starting</c>. Without a device runtime nothing runs anywhere from here, so
    /// nothing is refused.
    /// </summary>
    private AppError? NotActiveError() => runtimeStatus.Mode switch
    {
        RuntimeMode.Standby => AppError.Conflict(
            ErrorCodes.RuntimeStandby,
            "This instance is a read-only standby; make the change on the instance that runs the devices."),
        RuntimeMode.Starting when runtimeStatus.Enabled => AppError.Unavailable(
            ErrorCodes.RuntimeStarting,
            "The device runtime is starting; try again in a moment."),
        _ => null,
    };

    private static AppError InvalidConfig(Domain.Capabilities.ArgumentValidationResult validation)
    {
        var errors = validation.Errors
            .GroupBy(e => e.Argument is null ? ConfigPath : $"{ConfigPath}.{e.Argument}", StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => CommandArgumentErrors.CodeFor(e.Kind)).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        return AppError.Validation(
            ErrorCodes.IntegrationsInvalidConfig,
            "The configuration is invalid.",
            new Dictionary<string, object?>(StringComparer.Ordinal) { [AppError.ErrorsExtension] = errors });
    }

    /// <summary>The config without secret fields, and the secret fields' string values.</summary>
    private static (JsonObject Plain, Dictionary<string, string> Secrets) Split(IReadOnlyList<FieldSchema> fields, JsonElement config)
    {
        var plain = JsonNode.Parse(config.GetRawText()) as JsonObject ?? [];
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in fields.Where(f => f.Secret))
        {
            if (plain.TryGetPropertyValue(field.Key, out var value))
            {
                plain.Remove(field.Key);
                if (value is not null)
                {
                    secrets[field.Key] = value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString();
                }
            }
        }

        return (plain, secrets);
    }

    private static string IdOf(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);
}

using System.Text.Json;
using ControlHome.Domain.Devices;
using FluentValidation;

namespace ControlHome.Application.Integrations;

/// <summary>Starts a connector's setup flow (<c>POST /api/integrations/flows</c>, Admin).</summary>
/// <param name="ConnectorKey">A key from <c>GET /api/connectors</c>. Connectors without a flow of their own get a single form with their config fields.</param>
public sealed record StartFlowRequest(string ConnectorKey);

/// <summary>Answers the flow's current step (<c>POST /api/integrations/flows/{flowId}</c>, Admin).</summary>
/// <param name="Input">
/// A form's values (secret fields included, write-only), a choice (<c>{"choice":"key"}</c>), a pasted value, or an
/// empty object to poll a progress step or confirm an external page; <c>null</c> counts as an empty object.
/// </param>
public sealed record ContinueFlowRequest(JsonElement? Input);

/// <summary>Renames an integration and enables or disables it (<c>PUT /api/integrations/{id}</c>, Admin).</summary>
/// <param name="Name">1–128 characters.</param>
/// <param name="Enabled">A disabled integration's runtime does not run; its devices stay (availability Unknown).</param>
public sealed record UpdateIntegrationRequest(string Name, bool Enabled);

/// <summary>
/// One step of a setup or reauth flow. <paramref name="Type"/> says which members apply:
/// <list type="bullet">
/// <item><c>form</c>: <paramref name="Fields"/> (render with the schema form), <paramref name="Values"/> (initial values,
/// never secrets), <paramref name="Errors"/> (messages by field key after a rejected submission). Send the values.</item>
/// <item><c>externalUrl</c>: open <paramref name="Url"/> (https); <paramref name="PasteBack"/> is the field to paste the
/// result into, if any. Send <c>{ "&lt;pasteBack.key&gt;": "…" }</c> or <c>{}</c>.</item>
/// <item><c>progress</c>: waiting for something on the device for at most <paramref name="TimeoutSeconds"/>; poll with
/// <c>{}</c> (about every second) until the step changes.</item>
/// <item><c>choose</c>: pick one of <paramref name="Choices"/>; send <c>{ "choice": "&lt;key&gt;" }</c>.</item>
/// <item><c>done</c>: finished; <paramref name="IntegrationId"/> is the integration created or updated. The flow is gone.</item>
/// <item><c>abort</c>: the flow cannot continue: <paramref name="Reason"/> (stable snake_case) and
/// <paramref name="Message"/>. The flow is gone.</item>
/// </list>
/// Texts come from the connector in both UI languages.
/// </summary>
/// <param name="FlowId">Send the answer to <c>POST /api/integrations/flows/{flowId}</c>; flows expire 15 minutes after their last step.</param>
/// <param name="Type"><c>form</c>, <c>externalUrl</c>, <c>progress</c>, <c>choose</c>, <c>done</c> or <c>abort</c>.</param>
/// <param name="StepId">The connector's id of the step (for its form, progress, choose and external page steps).</param>
public sealed record FlowStepDto(
    Guid FlowId,
    string Type,
    string? StepId,
    LocalizedTextDto? Title,
    LocalizedTextDto? Description,
    IReadOnlyList<FieldSchemaDto> Fields,
    JsonElement? Values,
    IReadOnlyDictionary<string, LocalizedTextDto> Errors,
    string? Url,
    FieldSchemaDto? PasteBack,
    double? TimeoutSeconds,
    IReadOnlyList<FlowChoiceDto> Choices,
    Guid? IntegrationId,
    string? Reason,
    LocalizedTextDto? Message);

/// <summary>One option of a <c>choose</c> step.</summary>
public sealed record FlowChoiceDto(string Key, LocalizedTextDto Label, LocalizedTextDto? Description);

/// <summary>The <c>type</c> values of <see cref="FlowStepDto"/>.</summary>
public static class FlowStepTypes
{
    public const string Form = "form";
    public const string ExternalUrl = "externalUrl";
    public const string Progress = "progress";
    public const string Choose = "choose";
    public const string Done = "done";
    public const string Abort = "abort";
}

public sealed class StartFlowRequestValidator : AbstractValidator<StartFlowRequest>
{
    public StartFlowRequestValidator()
    {
        RuleFor(x => x.ConnectorKey).NotEmpty().MaximumLength(DeviceLimits.ConnectorKeyMaxLength);
    }
}

public sealed class ContinueFlowRequestValidator : AbstractValidator<ContinueFlowRequest>
{
    public ContinueFlowRequestValidator()
    {
        RuleFor(x => x.Input)
            .Must(input => input is null or { ValueKind: JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined })
            .WithMessage("'{PropertyName}' must be a JSON object.");
    }
}

public sealed class UpdateIntegrationRequestValidator : AbstractValidator<UpdateIntegrationRequest>
{
    public UpdateIntegrationRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(DeviceLimits.NameMaxLength).Must(n => !string.IsNullOrWhiteSpace(n))
            .WithMessage("'{PropertyName}' must not be blank.");
    }
}

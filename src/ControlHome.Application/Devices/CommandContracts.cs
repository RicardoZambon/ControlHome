using System.Text.Json;
using ControlHome.Domain.Devices;
using FluentValidation;

namespace ControlHome.Application.Devices;

/// <summary>A command for one endpoint (<c>POST /api/endpoints/{endpointId}/commands</c>).</summary>
/// <param name="CapabilityId">A capability of the endpoint, e.g. <c>switch</c>.</param>
/// <param name="Command">A command of that capability, e.g. <c>turnOn</c> or <c>setLevel</c>.</param>
/// <param name="Arguments">The arguments object (e.g. <c>{"level": 40}</c>); omit or <c>null</c> for none.</param>
/// <param name="IdempotencyKey">
/// Unique per user action (1–128 of <c>A-Z a-z 0-9 . _ : -</c>; a UUID is fine). The same key again within 10 minutes is
/// refused with 409 <c>commands.duplicate</c> and never reaches the device (at-most-once). Send the same key when retrying
/// the same action (e.g. after a step-up). The server generates one when omitted.
/// </param>
public sealed record CommandRequest(string CapabilityId, string Command, JsonElement? Arguments, string? IdempotencyKey);

/// <summary>The command was accepted; its progress arrives as hub <c>CommandStatus</c> messages with this id.</summary>
public sealed record CommandAcceptedDto(Guid CommandId);

public sealed class CommandRequestValidator : AbstractValidator<CommandRequest>
{
    public const int IdempotencyKeyMaxLength = 128;

    public CommandRequestValidator()
    {
        RuleFor(x => x.CapabilityId).NotEmpty().MaximumLength(DeviceLimits.CapabilityIdMaxLength);
        RuleFor(x => x.Command).NotEmpty().MaximumLength(64);
        RuleFor(x => x.IdempotencyKey)
            .NotEmpty()
            .MaximumLength(IdempotencyKeyMaxLength)
            .Matches("^[A-Za-z0-9._:-]+$")
            .When(x => x.IdempotencyKey is not null);
    }
}

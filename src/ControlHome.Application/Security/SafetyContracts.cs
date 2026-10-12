using FluentValidation;

namespace ControlHome.Application.Security;

/// <summary>Sets or changes the signed-in user's safety PIN (<c>PUT /api/auth/me/pin</c>).</summary>
/// <param name="CurrentPin">The PIN in use; required when one is set.</param>
/// <param name="NewPin">4–8 digits.</param>
public sealed record SetPinRequest(string? CurrentPin, string NewPin);

/// <summary>Proves the PIN before a safety-critical command (<c>POST /api/auth/step-up</c>).</summary>
public sealed record StepUpRequest(string Pin);

/// <summary>The signed-in user's safety PIN state (<c>GET /api/auth/me/pin</c>).</summary>
/// <param name="IsSet">Whether the user has a PIN (changing it then needs the current one).</param>
/// <param name="LockedForSeconds">Seconds left of a lockout after too many wrong PINs, or <c>null</c>.</param>
public sealed record SafetyPinStatusDto(bool IsSet, int? LockedForSeconds);

public static class SafetyPinRules
{
    public const int MinLength = 4;
    public const int MaxLength = 8;
    public const string Pattern = "^[0-9]{4,8}$";
}

public sealed class SetPinRequestValidator : AbstractValidator<SetPinRequest>
{
    public SetPinRequestValidator()
    {
        RuleFor(x => x.NewPin).NotEmpty().Matches(SafetyPinRules.Pattern).WithMessage("'{PropertyName}' must be 4 to 8 digits.");
        RuleFor(x => x.CurrentPin).MaximumLength(SafetyPinRules.MaxLength).When(x => x.CurrentPin is not null);
    }
}

public sealed class StepUpRequestValidator : AbstractValidator<StepUpRequest>
{
    public StepUpRequestValidator()
    {
        RuleFor(x => x.Pin).NotEmpty().Matches(SafetyPinRules.Pattern).WithMessage("'{PropertyName}' must be 4 to 8 digits.");
    }
}

using ControlHome.Application.Common;
using FluentValidation;

namespace ControlHome.Application.Security;

/// <summary>Sets or changes the signed-in user's safety PIN (<c>PUT /api/auth/me/pin</c>).</summary>
/// <param name="CurrentPin">The PIN in use, to change it (wrong ones count towards the safety lockout).</param>
/// <param name="CurrentPassword">
/// The account password: required to set the first PIN, and to replace a PIN without <paramref name="CurrentPin"/> (a
/// forgotten one). Checked like a sign-in (wrong ones count towards the account lockout).
/// </param>
/// <param name="NewPin">4–8 digits.</param>
public sealed record SetPinRequest(string? CurrentPin, string NewPin, string? CurrentPassword = null);

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
        RuleFor(x => x.CurrentPassword).NotEmpty().MaximumLength(ValidationRules.PasswordMaxLength).When(x => x.CurrentPassword is not null);
    }
}

public sealed class StepUpRequestValidator : AbstractValidator<StepUpRequest>
{
    public StepUpRequestValidator()
    {
        RuleFor(x => x.Pin).NotEmpty().Matches(SafetyPinRules.Pattern).WithMessage("'{PropertyName}' must be 4 to 8 digits.");
    }
}

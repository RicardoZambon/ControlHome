using ControlHome.Application.Common;
using FluentValidation;

namespace ControlHome.Application.Auth;

/// <summary>The signed-in user.</summary>
/// <param name="Locale"><c>pt-BR</c> or <c>en</c>.</param>
/// <param name="Theme"><c>light</c>, <c>dark</c> or <c>system</c>.</param>
public sealed record MeDto(Guid Id, string UserName, string DisplayName, string[] Roles, string Locale, string Theme);

/// <param name="RememberMe">Keep the session cookie across browser restarts (30-day sliding expiration).</param>
public sealed record LoginRequest(string UserName, string Password, bool RememberMe);

public sealed record UpdateProfileRequest(string DisplayName, string Locale, string Theme);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>Consumes a one-time reset token printed by the <c>reset-admin</c> command.</summary>
public sealed record ResetPasswordWithTokenRequest(string UserName, string Token, string NewPassword);

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.UserName).NotEmpty().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(ValidationRules.PasswordMaxLength);
    }
}

public sealed class UpdateProfileRequestValidator : AbstractValidator<UpdateProfileRequest>
{
    public UpdateProfileRequestValidator()
    {
        RuleFor(x => x.DisplayName).DisplayName();
        RuleFor(x => x.Locale).Locale();
        RuleFor(x => x.Theme).Theme();
    }
}

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().MaximumLength(ValidationRules.PasswordMaxLength);
        RuleFor(x => x.NewPassword).NewPassword();
    }
}

public sealed class ResetPasswordWithTokenRequestValidator : AbstractValidator<ResetPasswordWithTokenRequest>
{
    public ResetPasswordWithTokenRequestValidator()
    {
        RuleFor(x => x.UserName).NotEmpty().MaximumLength(256);
        RuleFor(x => x.Token).NotEmpty().MaximumLength(2048);
        RuleFor(x => x.NewPassword).NewPassword();
    }
}

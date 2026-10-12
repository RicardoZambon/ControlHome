using ControlHome.Application.Common;
using FluentValidation;

namespace ControlHome.Application.Users;

/// <summary>A household account as seen by an Admin.</summary>
/// <param name="Role"><c>Admin</c>, <c>Member</c> or <c>Viewer</c>.</param>
public sealed record UserDto(
    Guid Id,
    string UserName,
    string DisplayName,
    string Role,
    bool IsDisabled,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastLoginAtUtc);

/// <param name="Role"><c>Admin</c>, <c>Member</c> or <c>Viewer</c>.</param>
public sealed record CreateUserRequest(string UserName, string DisplayName, string Password, string Role);

/// <param name="Role"><c>Admin</c>, <c>Member</c> or <c>Viewer</c>.</param>
public sealed record UpdateUserRequest(string DisplayName, string Role, bool IsDisabled);

/// <summary>An Admin sets a new password for another user.</summary>
public sealed record ResetPasswordRequest(string NewPassword);

public sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.UserName).UserName();
        RuleFor(x => x.DisplayName).DisplayName();
        RuleFor(x => x.Password).NewPassword();
        RuleFor(x => x.Role).Role();
    }
}

public sealed class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserRequestValidator()
    {
        RuleFor(x => x.DisplayName).DisplayName();
        RuleFor(x => x.Role).Role();
    }
}

public sealed class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(x => x.NewPassword).NewPassword();
    }
}

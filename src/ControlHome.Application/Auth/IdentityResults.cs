using ControlHome.Application.Common;
using Microsoft.AspNetCore.Identity;

namespace ControlHome.Application.Auth;

/// <summary>Maps a failed Identity <see cref="IdentityResult"/> to an <see cref="AppError"/>.</summary>
public static class IdentityResults
{
    private const string DuplicateUserName = nameof(IdentityErrorDescriber.DuplicateUserName);
    private const string PasswordMismatch = nameof(IdentityErrorDescriber.PasswordMismatch);
    private const string InvalidToken = nameof(IdentityErrorDescriber.InvalidToken);
    private const string ConcurrencyFailure = nameof(IdentityErrorDescriber.ConcurrencyFailure);

    /// <summary>
    /// Another request changed the row first (<c>ConcurrencyFailure</c>) → 409 <c>request.conflict</c>; duplicate user name →
    /// 409 <c>users.duplicate_username</c>; wrong current password → 400 <c>auth.password_incorrect</c>; bad reset token →
    /// 400 <c>auth.reset_invalid</c>; anything else → 400 <c>validation.failed</c> with the messages under
    /// <c>errors[<paramref name="field"/>]</c>.
    /// </summary>
    public static AppError ToAppError(this IdentityResult result, string field)
    {
        ArgumentNullException.ThrowIfNull(result);
        var codes = result.Errors.Select(e => e.Code).ToHashSet(StringComparer.Ordinal);

        if (codes.Contains(ConcurrencyFailure))
        {
            return Conflict();
        }

        if (codes.Contains(DuplicateUserName))
        {
            return AppError.Conflict(ErrorCodes.UsersDuplicateUserName, "That user name is already taken.");
        }

        if (codes.Contains(PasswordMismatch))
        {
            return AppError.Validation(ErrorCodes.AuthPasswordIncorrect, "The current password is not correct.");
        }

        if (codes.Contains(InvalidToken))
        {
            return AppError.Validation(ErrorCodes.AuthResetInvalid, "The reset link is invalid or has expired.");
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [field] = result.Errors.Select(e => e.Description).Distinct(StringComparer.Ordinal).ToArray(),
        };
        return AppError.Validation(
            ErrorCodes.ValidationFailed,
            "One or more fields are invalid.",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["errors"] = errors });
    }

    /// <summary>409 <c>request.conflict</c>: the data changed while this request was saving it; trying again may succeed.</summary>
    public static AppError Conflict() =>
        AppError.Conflict(ErrorCodes.RequestConflict, "The data changed while saving; try again.");
}

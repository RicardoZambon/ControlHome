using System.Globalization;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Auditing;
using ControlHome.Application.Auth;
using ControlHome.Application.Common;
using ControlHome.Application.Identity;
using Microsoft.AspNetCore.Identity;

namespace ControlHome.Application.Security;

/// <summary>
/// The signed-in user's safety PIN and step-up: the PIN (4–8 digits) is hashed with the Identity password hasher and stored
/// in <see cref="AppUser.SafetyPinHash"/>; a correct PIN grants one safety-critical command within 60 s
/// (<see cref="SafetyGrantStore"/>); 5 wrong PINs within 15 min (step-up or PIN change) lock the user out of
/// safety-critical commands (423 <c>safety.locked</c>). Every attempt is audited.
/// </summary>
public sealed class StepUpService(
    UserManager<AppUser> userManager,
    SafetyGrantStore grants,
    ICurrentActor actor,
    IAuditLog auditLog)
{
    public const string UserTarget = "user";

    public async Task<Result<SafetyPinStatusDto>> GetStatusAsync()
    {
        var user = await CurrentUserAsync();
        if (user is null)
        {
            return NotSignedIn();
        }

        return new SafetyPinStatusDto(user.SafetyPinHash is not null, Seconds(grants.LockedFor(user.Id)));
    }

    /// <summary>
    /// Sets the PIN (first time) or changes it (<see cref="SetPinRequest.CurrentPin"/> must match). Errors: 400
    /// <c>safety.pin_incorrect</c> (counts as a failed attempt), 423 <c>safety.locked</c>.
    /// </summary>
    public async Task<Result> SetPinAsync(SetPinRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await CurrentUserAsync();
        if (user is null)
        {
            return NotSignedIn();
        }

        var changing = user.SafetyPinHash is not null;
        if (changing)
        {
            if (grants.LockedFor(user.Id) is { } locked)
            {
                return Locked(locked);
            }

            if (request.CurrentPin is null || !Verify(user, request.CurrentPin))
            {
                return await FailAsync(user, "set_pin", cancellationToken);
            }
        }

        user.SafetyPinHash = userManager.PasswordHasher.HashPassword(user, request.NewPin);
        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return result.ToAppError("newPin");
        }

        await auditLog.WriteAsync(
            new AuditEvent(AuditActions.SafetyPinSet, UserTarget, IdOf(user), new { user.UserName, Changed = changing }),
            cancellationToken);
        return Result.Success();
    }

    /// <summary>
    /// Checks the PIN and grants one safety-critical command within 60 s. Errors: 409 <c>safety.pin_not_set</c>, 400
    /// <c>safety.pin_incorrect</c>, 423 <c>safety.locked</c> (with <c>retryAfterSeconds</c>).
    /// </summary>
    public async Task<Result> StepUpAsync(StepUpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await CurrentUserAsync();
        if (user is null)
        {
            return NotSignedIn();
        }

        if (grants.LockedFor(user.Id) is { } locked)
        {
            return Locked(locked);
        }

        if (user.SafetyPinHash is null)
        {
            return AppError.Conflict(ErrorCodes.SafetyPinNotSet, "Set a safety PIN in your profile first.");
        }

        if (!Verify(user, request.Pin))
        {
            return await FailAsync(user, "step_up", cancellationToken);
        }

        grants.Grant(user.Id);
        await auditLog.WriteAsync(
            new AuditEvent(AuditActions.SafetyStepUpSucceeded, UserTarget, IdOf(user), new { user.UserName }),
            cancellationToken);
        return Result.Success();
    }

    private bool Verify(AppUser user, string pin)
    {
        if (user.SafetyPinHash is null)
        {
            return false;
        }

        var result = userManager.PasswordHasher.VerifyHashedPassword(user, user.SafetyPinHash, pin);
        return result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
    }

    private async Task<AppError> FailAsync(AppUser user, string step, CancellationToken cancellationToken)
    {
        var locked = grants.RecordFailure(user.Id);
        await auditLog.WriteAsync(
            new AuditEvent(AuditActions.SafetyStepUpFailed, UserTarget, IdOf(user), new { user.UserName, Step = step }),
            cancellationToken);
        if (locked is null)
        {
            return AppError.Validation(ErrorCodes.SafetyPinIncorrect, "The PIN is not correct.");
        }

        await auditLog.WriteAsync(
            new AuditEvent(
                AuditActions.SafetyStepUpLocked,
                UserTarget,
                IdOf(user),
                new { user.UserName, LockoutSeconds = Seconds(locked) }),
            cancellationToken);
        return Locked(locked.Value);
    }

    /// <summary>423 <c>safety.locked</c> with <c>retryAfterSeconds</c>.</summary>
    public static AppError Locked(TimeSpan remaining) =>
        AppError.Locked(
            ErrorCodes.SafetyLocked,
            "Too many wrong PINs; safety-critical commands are locked for a while.",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["retryAfterSeconds"] = Seconds(remaining) });

    private static int? Seconds(TimeSpan? remaining) =>
        remaining is { } value ? Math.Max(1, (int)Math.Ceiling(value.TotalSeconds)) : null;

    private static string IdOf(AppUser user) => user.Id.ToString("D", CultureInfo.InvariantCulture);

    private static AppError NotSignedIn() => AppError.Unauthorized(ErrorCodes.AuthRequired, "Sign in to continue.");

    private async Task<AppUser?> CurrentUserAsync()
    {
        if (actor.UserId is not { } id)
        {
            return null;
        }

        var user = await userManager.FindByIdAsync(id.ToString("D", CultureInfo.InvariantCulture));
        return user is { IsDisabled: false } ? user : null;
    }
}

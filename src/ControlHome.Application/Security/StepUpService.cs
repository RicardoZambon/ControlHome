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
/// safety-critical commands (423 <c>safety.locked</c>). PIN checks of one user run one at a time, so concurrent guesses
/// cannot get past the lockout. Setting the first PIN, or replacing a forgotten one, needs the account password (checked
/// like a sign-in): a session alone never gives safety-critical access. Every attempt is audited.
/// </summary>
public sealed class StepUpService(
    UserManager<AppUser> userManager,
    SafetyGrantStore grants,
    AuthService auth,
    ICurrentActor actor,
    IAuditLog auditLog)
{
    public const string UserTarget = "user";

    /// <summary>The <c>purpose</c> of the password check that authorizes a PIN change (audit).</summary>
    public const string PasswordPurpose = "safety_pin";

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
    /// Sets the PIN. Changing it needs <see cref="SetPinRequest.CurrentPin"/>; setting the first one, or replacing one
    /// without the current PIN (forgotten), needs <see cref="SetPinRequest.CurrentPassword"/>. Errors: 400
    /// <c>safety.password_required</c>; 400 <c>safety.pin_incorrect</c> (counts as a failed PIN attempt) or 423
    /// <c>safety.locked</c>; 400 <c>auth.password_incorrect</c> (counts towards the account lockout) or 423
    /// <c>auth.locked_out</c>.
    /// </summary>
    public async Task<Result> SetPinAsync(SetPinRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (actor.UserId is not { } userId)
        {
            return NotSignedIn();
        }

        using var attempt = await grants.BeginAttemptAsync(userId, cancellationToken);
        var user = await CurrentUserAsync();
        if (user is null)
        {
            return NotSignedIn();
        }

        var changing = user.SafetyPinHash is not null;
        var viaPin = changing && request.CurrentPin is not null;
        if (viaPin)
        {
            if (grants.LockedFor(user.Id) is { } locked)
            {
                return Locked(locked);
            }

            if (!Verify(user, request.CurrentPin!))
            {
                return await FailAsync(user, "set_pin", cancellationToken);
            }
        }
        else
        {
            if (string.IsNullOrEmpty(request.CurrentPassword))
            {
                return AppError.Validation(
                    ErrorCodes.SafetyPasswordRequired,
                    changing
                        ? "Enter your current PIN, or your account password to replace a forgotten one."
                        : "Enter your account password to set your safety PIN.");
            }

            var verified = await auth.VerifyPasswordAsync(request.CurrentPassword, PasswordPurpose, cancellationToken);
            if (verified.IsFailure)
            {
                return verified.Error;
            }

            // The check may have written the row (cleared counters): continue with the stored version.
            user = await CurrentUserAsync();
            if (user is null)
            {
                return NotSignedIn();
            }
        }

        user.SafetyPinHash = userManager.PasswordHasher.HashPassword(user, request.NewPin);
        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return result.ToAppError("newPin");
        }

        await auditLog.WriteAsync(
            new AuditEvent(
                AuditActions.SafetyPinSet,
                UserTarget,
                IdOf(user),
                new { user.UserName, Changed = changing, Via = viaPin ? "pin" : "password" }),
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
        if (actor.UserId is not { } userId)
        {
            return NotSignedIn();
        }

        // One check at a time per user: a check waiting here sees the lockout an earlier one started and never verifies.
        using var attempt = await grants.BeginAttemptAsync(userId, cancellationToken);
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

        if (!grants.TryGrant(user.Id))
        {
            return Locked(grants.LockedFor(user.Id) ?? SafetyGrantStore.LockDuration);
        }

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

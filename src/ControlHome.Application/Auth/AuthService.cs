using System.Globalization;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Auditing;
using ControlHome.Application.Common;
using ControlHome.Application.Identity;
using Microsoft.AspNetCore.Identity;

namespace ControlHome.Application.Auth;

/// <summary>
/// Sign-in, sign-out and the signed-in user's own account. Every check of a user's password (sign-in and the current
/// password of a password change) runs under that user's sign-in lock and counts towards one escalating lockout
/// (<see cref="LockoutPolicy"/>); unknown user names are counted the same way (<see cref="UnknownUserLockouts"/>), so
/// responses never reveal which names exist. Every attempt is audited.
/// </summary>
public sealed class AuthService(
    UserManager<AppUser> userManager,
    ISessionSignIn session,
    IExclusiveTransaction exclusive,
    IAuditLog auditLog,
    ICurrentActor actor,
    UnknownUserLockouts unknownUsers,
    TimeProvider timeProvider)
{
    public const string UserTarget = "user";

    private const int MaxAuditedUserNameLength = 64;

    /// <summary>How often a password check runs again when another request changed the user row at the same moment.</summary>
    private const int MaxConflictRounds = 3;

    private static readonly Lock _dummyHashLock = new();
    private static string? _dummyHash;

    /// <remarks>
    /// <paramref name="cancellationToken"/> only stops a request whose client is already gone: once a password is checked, the
    /// attempt is always counted and audited (otherwise a client could disconnect mid-check to guess without being counted).
    /// </remarks>
    public async Task<Result<MeDto>> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var user = await userManager.FindByNameAsync(request.UserName);
        if (user is null)
        {
            return await RejectUnknownUserAsync(request);
        }

        // One attempt per user at a time, so concurrent failures are all counted.
        var attempt = await RunUnderSignInLockAsync(user.Id, token => CheckCredentialsAsync(user.Id, request, token));
        if (attempt.IsFailure)
        {
            return attempt.Error;
        }

        if (attempt.Value.Error is { } error)
        {
            return error;
        }

        var signedIn = attempt.Value.User!;
        await session.SignInAsync(signedIn, request.RememberMe);
        await auditLog.WriteAsync(
            new AuditEvent(AuditActions.LoginSucceeded, UserTarget, IdOf(signedIn), new { signedIn.UserName }),
            CancellationToken.None);
        return await userManager.ToMeDtoAsync(signedIn);
    }

    /// <summary>Ends the session (no-op when not signed in).</summary>
    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        if (actor.UserId is { } id)
        {
            await auditLog.WriteAsync(
                new AuditEvent(AuditActions.Logout, UserTarget, id.ToString("D", CultureInfo.InvariantCulture)),
                cancellationToken);
        }

        await session.SignOutAsync();
    }

    public async Task<Result<MeDto>> GetMeAsync()
    {
        var user = await CurrentUserAsync();
        return user is null ? NotSignedIn() : await userManager.ToMeDtoAsync(user);
    }

    public async Task<Result<MeDto>> UpdateProfileAsync(UpdateProfileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await CurrentUserAsync();
        if (user is null)
        {
            return NotSignedIn();
        }

        user.DisplayName = request.DisplayName.Trim();
        user.Locale = request.Locale;
        user.Theme = request.Theme;
        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return result.ToAppError("displayName");
        }

        return await userManager.ToMeDtoAsync(user);
    }

    /// <summary>
    /// Changes the password and re-issues the session cookie (the security stamp changes, ending other sessions). The current
    /// password is checked like a sign-in: under the user's sign-in lock, refused with 423 while the account is locked out,
    /// and a wrong one counts towards the same lockout (audited as <c>auth.password.change_failed</c>).
    /// </summary>
    /// <remarks>As for <see cref="LoginAsync"/>, a client disconnect does not stop an attempt from being counted.</remarks>
    public async Task<Result> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (actor.UserId is not { } userId)
        {
            return NotSignedIn();
        }

        var attempt = await RunUnderSignInLockAsync(userId, token => ChangePasswordLockedAsync(userId, request, token));
        if (attempt.IsFailure)
        {
            return attempt.Error;
        }

        if (attempt.Value.Error is { } error)
        {
            return error;
        }

        await session.RefreshAsync(attempt.Value.User!);
        return Result.Success();
    }

    /// <summary>
    /// Consumes a one-time token printed by <c>reset-admin</c>: in one write, sets the new password (which changes the
    /// security stamp, so the token cannot be used twice) and clears any lockout. Runs under the user's sign-in lock.
    /// </summary>
    public async Task<Result> ResetPasswordWithTokenAsync(ResetPasswordWithTokenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await userManager.FindByNameAsync(request.UserName);
        if (user is null)
        {
            return ResetInvalid();
        }

        var userId = user.Id;
        AppUser? reset = null;
        var result = await exclusive.RunAsync<bool>(
            LoginLockName(userId),
            async token =>
            {
                reset = await userManager.FindByIdAsync(Id(userId));
                if (reset is null)
                {
                    return ResetInvalid();
                }

                // Cleared first, so ResetPasswordAsync saves the counters together with the new password.
                ClearLockout(reset);
                var changed = await userManager.ResetPasswordAsync(reset, request.Token, request.NewPassword);
                if (!changed.Succeeded)
                {
                    return changed.ToAppError("newPassword");
                }

                await auditLog.WriteAsync(
                    new AuditEvent(AuditActions.AdminReset, UserTarget, IdOf(reset), new { reset.UserName, Step = "password_reset" }),
                    token);
                return true;
            },
            token => userManager.IsStoredAsync(reset, token),
            cancellationToken);

        return result.IsSuccess ? Result.Success() : result.Error;
    }

    internal static void ClearLockout(AppUser user)
    {
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        user.ConsecutiveLockouts = 0;
    }

    private static string LoginLockName(Guid userId) => $"ControlHome.Login.{userId:N}";

    private static string Id(Guid userId) => userId.ToString("D", CultureInfo.InvariantCulture);

    private static string IdOf(AppUser user) => Id(user.Id);

    private static AppError InvalidCredentials() =>
        AppError.Unauthorized(ErrorCodes.AuthInvalidCredentials, "The user name or password is not correct.");

    private static AppError NotSignedIn() => AppError.Unauthorized(ErrorCodes.AuthRequired, "Sign in to continue.");

    private static AppError PasswordIncorrect() =>
        AppError.Validation(ErrorCodes.AuthPasswordIncorrect, "The current password is not correct.");

    private static AppError ResetInvalid() =>
        AppError.Validation(ErrorCodes.AuthResetInvalid, "The reset link is invalid or has expired.");

    private static string Truncate(string value) =>
        value.Length <= MaxAuditedUserNameLength ? value : value[..MaxAuditedUserNameLength];

    private static bool IsLockedOut(AppUser user, DateTimeOffset now, out TimeSpan remaining)
    {
        remaining = user.LockoutEnabled && user.LockoutEnd is { } lockedUntil && lockedUntil > now ? lockedUntil - now : TimeSpan.Zero;
        return remaining > TimeSpan.Zero;
    }

    /// <summary>
    /// Runs a password check under the user's sign-in lock. The request token is deliberately not passed on (see
    /// <see cref="LoginAsync"/>). When another request changed the user row between the check's read and write
    /// (<c>request.conflict</c>, rolled back), the check runs again on fresh data.
    /// </summary>
    private async Task<Result<CredentialCheck>> RunUnderSignInLockAsync(
        Guid userId,
        Func<CancellationToken, Task<Result<CredentialCheck>>> check)
    {
        for (var round = 1; ; round++)
        {
            AppUser? checkedUser = null;
            var result = await exclusive.RunAsync(
                LoginLockName(userId),
                async token =>
                {
                    var outcome = await check(token);
                    checkedUser = outcome.IsSuccess ? outcome.Value.User : null;
                    return outcome;
                },
                token => userManager.IsStoredAsync(checkedUser, token),
                CancellationToken.None);

            if (result.IsFailure && result.Error.Code == ErrorCodes.RequestConflict && round < MaxConflictRounds)
            {
                continue;
            }

            return result;
        }
    }

    /// <summary>Runs inside the per-user lock and transaction; "succeeds" whenever counters and audit entries must be committed.</summary>
    private async Task<Result<CredentialCheck>> CheckCredentialsAsync(Guid userId, LoginRequest request, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(Id(userId));
        if (user is null)
        {
            VerifyAgainstDummyHash(request.Password);
            return new CredentialCheck(null, InvalidCredentials());
        }

        var now = timeProvider.GetUtcNow();
        if (IsLockedOut(user, now, out var remaining))
        {
            await AuditLoginFailureAsync(user, request.UserName, "locked_out", cancellationToken);
            return new CredentialCheck(user, LockedOut(remaining));
        }

        if (!await userManager.CheckPasswordAsync(user, request.Password))
        {
            var counted = await CountFailedPasswordAsync(user, now);
            if (counted.IsFailure)
            {
                return counted.Error;
            }

            await AuditLoginFailureAsync(user, request.UserName, "invalid_password", cancellationToken);
            return await FailedPasswordOutcomeAsync(user, counted.Value, InvalidCredentials(), cancellationToken);
        }

        if (user.IsDisabled)
        {
            await AuditLoginFailureAsync(user, request.UserName, "disabled", cancellationToken);
            return new CredentialCheck(user, AppError.Forbidden(ErrorCodes.AuthDisabled, "This account is disabled."));
        }

        ClearLockout(user);
        user.LastLoginAtUtc = now;
        var updated = await userManager.UpdateAsync(user);
        if (!updated.Succeeded)
        {
            return updated.ToAppError("userName");
        }

        return new CredentialCheck(user, null);
    }

    /// <summary>Runs inside the per-user lock and transaction, like <see cref="CheckCredentialsAsync"/>.</summary>
    private async Task<Result<CredentialCheck>> ChangePasswordLockedAsync(
        Guid userId,
        ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(Id(userId));
        if (user is null || user.IsDisabled)
        {
            return NotSignedIn();
        }

        var now = timeProvider.GetUtcNow();
        if (IsLockedOut(user, now, out var remaining))
        {
            await AuditPasswordChangeFailureAsync(user, "locked_out", cancellationToken);
            return new CredentialCheck(user, LockedOut(remaining));
        }

        if (!await userManager.CheckPasswordAsync(user, request.CurrentPassword))
        {
            var counted = await CountFailedPasswordAsync(user, now);
            if (counted.IsFailure)
            {
                return counted.Error;
            }

            await AuditPasswordChangeFailureAsync(user, "invalid_current_password", cancellationToken);
            return await FailedPasswordOutcomeAsync(user, counted.Value, PasswordIncorrect(), cancellationToken);
        }

        // One write: the new password hash, a new security stamp and the cleared lockout counters (the current password was
        // just verified, so a fresh reset token stands in for it instead of hashing it a second time).
        ClearLockout(user);
        var resetToken = await userManager.GeneratePasswordResetTokenAsync(user);
        var changed = await userManager.ResetPasswordAsync(user, resetToken, request.NewPassword);
        if (!changed.Succeeded)
        {
            return changed.ToAppError("newPassword");
        }

        await auditLog.WriteAsync(new AuditEvent(AuditActions.PasswordChanged, UserTarget, IdOf(user)), cancellationToken);
        return new CredentialCheck(user, null);
    }

    /// <summary>
    /// Counts a wrong password: every <c>MaxFailedAccessAttempts</c>-th failure starts a lockout (escalating with each
    /// lockout in a row). Fails with <c>request.conflict</c> when another request changed the row meanwhile.
    /// </summary>
    private async Task<Result<FailedPassword>> CountFailedPasswordAsync(AppUser user, DateTimeOffset now)
    {
        if (!user.LockoutEnabled)
        {
            return new FailedPassword(null);
        }

        TimeSpan? lockout = null;
        user.AccessFailedCount++;
        if (user.AccessFailedCount >= userManager.Options.Lockout.MaxFailedAccessAttempts)
        {
            lockout = LockoutPolicy.Duration(userManager.Options.Lockout.DefaultLockoutTimeSpan, user.ConsecutiveLockouts);
            user.LockoutEnd = now + lockout.Value;
            user.AccessFailedCount = 0;
            user.ConsecutiveLockouts++;
        }

        var updated = await userManager.UpdateAsync(user);
        return updated.Succeeded ? new FailedPassword(lockout) : updated.ToAppError("password");
    }

    /// <summary><paramref name="error"/>, or 423 (audited <c>auth.login.locked_out</c>) when this failure started a lockout.</summary>
    private async Task<CredentialCheck> FailedPasswordOutcomeAsync(
        AppUser user,
        FailedPassword failure,
        AppError error,
        CancellationToken cancellationToken)
    {
        if (failure.Lockout is not { } lockout)
        {
            return new CredentialCheck(user, error);
        }

        await auditLog.WriteAsync(
            new AuditEvent(
                AuditActions.LoginLockedOut,
                UserTarget,
                IdOf(user),
                new { user.UserName, LockoutSeconds = (int)lockout.TotalSeconds, user.ConsecutiveLockouts }),
            cancellationToken);
        return new CredentialCheck(user, LockedOut(lockout));
    }

    /// <summary>
    /// An unknown user name gets the same answers as a real account would: 401 after spending a password check's time, and
    /// 423 once it has as many failures as would lock a real account.
    /// </summary>
    private async Task<Result<MeDto>> RejectUnknownUserAsync(LoginRequest request)
    {
        var name = userManager.NormalizeName(request.UserName);
        AppError error;
        if (unknownUsers.RemainingLockout(name) is { } remaining)
        {
            error = LockedOut(remaining);
        }
        else
        {
            // Spend about the same time as a real password check, so response times don't reveal which user names exist.
            VerifyAgainstDummyHash(request.Password);
            var lockout = unknownUsers.RecordFailure(
                name,
                userManager.Options.Lockout.MaxFailedAccessAttempts,
                userManager.Options.Lockout.DefaultLockoutTimeSpan);
            error = lockout is { } duration ? LockedOut(duration) : InvalidCredentials();
        }

        await AuditLoginFailureAsync(null, request.UserName, "unknown_user", CancellationToken.None);
        return error;
    }

    private Task AuditLoginFailureAsync(AppUser? user, string attemptedUserName, string reason, CancellationToken cancellationToken) =>
        auditLog.WriteAsync(
            new AuditEvent(
                AuditActions.LoginFailed,
                user is null ? null : UserTarget,
                user is null ? null : IdOf(user),
                new { UserName = Truncate(attemptedUserName), Reason = reason }),
            cancellationToken);

    private Task AuditPasswordChangeFailureAsync(AppUser user, string reason, CancellationToken cancellationToken) =>
        auditLog.WriteAsync(
            new AuditEvent(AuditActions.PasswordChangeFailed, UserTarget, IdOf(user), new { user.UserName, Reason = reason }),
            cancellationToken);

    private static AppError LockedOut(TimeSpan remaining)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        return AppError.Locked(
            ErrorCodes.AuthLockedOut,
            "Too many failed sign-in attempts; try again later.",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["retryAfterSeconds"] = seconds });
    }

    /// <summary>The signed-in, enabled user (null when signed out, deleted or disabled).</summary>
    private async Task<AppUser?> CurrentUserAsync()
    {
        if (actor.UserId is not { } id)
        {
            return null;
        }

        var user = await userManager.FindByIdAsync(Id(id));
        return user is { IsDisabled: false } ? user : null;
    }

    private void VerifyAgainstDummyHash(string password)
    {
        string hash;
        lock (_dummyHashLock)
        {
            hash = _dummyHash ??= userManager.PasswordHasher.HashPassword(new AppUser(), Guid.NewGuid().ToString("N"));
        }

        _ = userManager.PasswordHasher.VerifyHashedPassword(new AppUser(), hash, password);
    }

    /// <summary>
    /// A password check's outcome: <see cref="Error"/> null means success. <see cref="User"/> is the row as the check left it
    /// (null when the user disappeared), for the post-commit verification and, on success, the session.
    /// </summary>
    private sealed record CredentialCheck(AppUser? User, AppError? Error);

    /// <summary>A counted wrong password; <see cref="Lockout"/> is set when it started a lockout.</summary>
    private sealed record FailedPassword(TimeSpan? Lockout);
}

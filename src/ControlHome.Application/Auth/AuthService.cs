using System.Globalization;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Auditing;
using ControlHome.Application.Common;
using ControlHome.Application.Identity;
using Microsoft.AspNetCore.Identity;

namespace ControlHome.Application.Auth;

/// <summary>
/// Sign-in, sign-out and the signed-in user's own account. Failed sign-ins count towards an escalating lockout
/// (<see cref="LockoutPolicy"/>); every attempt is audited.
/// </summary>
public sealed class AuthService(
    UserManager<AppUser> userManager,
    ISessionSignIn session,
    IExclusiveTransaction exclusive,
    IAuditLog auditLog,
    ICurrentActor actor,
    TimeProvider timeProvider)
{
    public const string UserTarget = "user";

    private const int MaxAuditedUserNameLength = 64;

    private static readonly Lock _dummyHashLock = new();
    private static string? _dummyHash;

    public async Task<Result<MeDto>> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await userManager.FindByNameAsync(request.UserName);
        if (user is null)
        {
            // Spend about the same time as a real password check, so response times don't reveal which user names exist.
            VerifyAgainstDummyHash(request.Password);
            await AuditFailureAsync(null, request.UserName, "unknown_user", cancellationToken);
            return InvalidCredentials();
        }

        // One attempt per user at a time, so concurrent failures are all counted.
        var attempt = await exclusive.RunAsync(
            LoginLockName(user.Id),
            token => CheckCredentialsAsync(user.Id, request, token),
            cancellationToken);
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
            cancellationToken);
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

    /// <summary>Changes the password and re-issues the session cookie (the security stamp changes, ending other sessions).</summary>
    public async Task<Result> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await CurrentUserAsync();
        if (user is null)
        {
            return NotSignedIn();
        }

        var result = await userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            return result.ToAppError("newPassword");
        }

        await session.RefreshAsync(user);
        await auditLog.WriteAsync(new AuditEvent(AuditActions.PasswordChanged, UserTarget, IdOf(user)), cancellationToken);
        return Result.Success();
    }

    /// <summary>
    /// Consumes a one-time token printed by <c>reset-admin</c>: sets the new password (which changes the security stamp, so
    /// the token cannot be used twice) and clears any lockout.
    /// </summary>
    public async Task<Result> ResetPasswordWithTokenAsync(ResetPasswordWithTokenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await userManager.FindByNameAsync(request.UserName);
        if (user is null)
        {
            return AppError.Validation(ErrorCodes.AuthResetInvalid, "The reset link is invalid or has expired.");
        }

        var result = await userManager.ResetPasswordAsync(user, request.Token, request.NewPassword);
        if (!result.Succeeded)
        {
            return result.ToAppError("newPassword");
        }

        ClearLockout(user);
        await userManager.UpdateAsync(user);
        await auditLog.WriteAsync(
            new AuditEvent(AuditActions.AdminReset, UserTarget, IdOf(user), new { user.UserName, Step = "password_reset" }),
            cancellationToken);
        return Result.Success();
    }

    internal static void ClearLockout(AppUser user)
    {
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        user.ConsecutiveLockouts = 0;
    }

    private static string LoginLockName(Guid userId) => $"ControlHome.Login.{userId:N}";

    private static string IdOf(AppUser user) => user.Id.ToString("D", CultureInfo.InvariantCulture);

    private static AppError InvalidCredentials() =>
        AppError.Unauthorized(ErrorCodes.AuthInvalidCredentials, "The user name or password is not correct.");

    private static AppError NotSignedIn() => AppError.Unauthorized(ErrorCodes.AuthRequired, "Sign in to continue.");

    private static string Truncate(string value) =>
        value.Length <= MaxAuditedUserNameLength ? value : value[..MaxAuditedUserNameLength];

    /// <summary>Runs inside the per-user lock and transaction; always "succeeds" so counters and audit entries are committed.</summary>
    private async Task<Result<LoginAttempt>> CheckCredentialsAsync(Guid userId, LoginRequest request, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString("D", CultureInfo.InvariantCulture));
        if (user is null)
        {
            VerifyAgainstDummyHash(request.Password);
            return new LoginAttempt(null, InvalidCredentials());
        }

        var now = timeProvider.GetUtcNow();
        if (user.LockoutEnabled && user.LockoutEnd is { } lockedUntil && lockedUntil > now)
        {
            await AuditFailureAsync(user, request.UserName, "locked_out", cancellationToken);
            return new LoginAttempt(null, LockedOut(lockedUntil - now));
        }

        if (!await userManager.CheckPasswordAsync(user, request.Password))
        {
            return await RecordFailedPasswordAsync(user, request.UserName, now, cancellationToken);
        }

        if (user.IsDisabled)
        {
            await AuditFailureAsync(user, request.UserName, "disabled", cancellationToken);
            return new LoginAttempt(null, AppError.Forbidden(ErrorCodes.AuthDisabled, "This account is disabled."));
        }

        ClearLockout(user);
        user.LastLoginAtUtc = now;
        var updated = await userManager.UpdateAsync(user);
        if (!updated.Succeeded)
        {
            return updated.ToAppError("userName");
        }

        return new LoginAttempt(user, null);
    }

    private async Task<LoginAttempt> RecordFailedPasswordAsync(AppUser user, string attemptedUserName, DateTimeOffset now, CancellationToken cancellationToken)
    {
        TimeSpan? lockout = null;
        if (user.LockoutEnabled)
        {
            user.AccessFailedCount++;
            if (user.AccessFailedCount >= userManager.Options.Lockout.MaxFailedAccessAttempts)
            {
                lockout = LockoutPolicy.Duration(userManager.Options.Lockout.DefaultLockoutTimeSpan, user.ConsecutiveLockouts);
                user.LockoutEnd = now + lockout.Value;
                user.AccessFailedCount = 0;
                user.ConsecutiveLockouts++;
            }

            await userManager.UpdateAsync(user);
        }

        await AuditFailureAsync(user, attemptedUserName, "invalid_password", cancellationToken);
        if (lockout is null)
        {
            return new LoginAttempt(null, InvalidCredentials());
        }

        await auditLog.WriteAsync(
            new AuditEvent(
                AuditActions.LoginLockedOut,
                UserTarget,
                IdOf(user),
                new { user.UserName, LockoutSeconds = (int)lockout.Value.TotalSeconds, user.ConsecutiveLockouts }),
            cancellationToken);
        return new LoginAttempt(null, LockedOut(lockout.Value));
    }

    private Task AuditFailureAsync(AppUser? user, string attemptedUserName, string reason, CancellationToken cancellationToken) =>
        auditLog.WriteAsync(
            new AuditEvent(
                AuditActions.LoginFailed,
                user is null ? null : UserTarget,
                user is null ? null : IdOf(user),
                new { UserName = Truncate(attemptedUserName), Reason = reason }),
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

        var user = await userManager.FindByIdAsync(id.ToString("D", CultureInfo.InvariantCulture));
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

    private sealed record LoginAttempt(AppUser? User, AppError? Error);
}

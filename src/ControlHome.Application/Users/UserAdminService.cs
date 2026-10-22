using System.Globalization;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Auditing;
using ControlHome.Application.Auth;
using ControlHome.Application.Common;
using ControlHome.Application.Identity;
using ControlHome.Application.Security;
using ControlHome.Domain.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Application.Users;

/// <summary>
/// Household accounts, managed by an Admin. Every change runs under the application lock <see cref="LockName"/>, so the
/// last-active-Admin rule (<c>users.last_admin</c>) holds even when two admins act at the same time, and first re-checks in
/// the database that the caller is still an enabled Admin (403 <c>auth.forbidden</c> otherwise): the role in a session
/// cookie can be up to a minute old, and a demoted or disabled admin must not be able to undo that meanwhile. Disabling a
/// user, changing their role or resetting their password updates their security stamp, which ends their existing sessions;
/// those changes and deleting a user also close the user's live hub connections at once (<see cref="IUserSessionRevoker"/>).
/// </summary>
public sealed class UserAdminService(
    UserManager<AppUser> userManager,
    IExclusiveTransaction exclusive,
    IControlHomeDb db,
    ISessionSignIn session,
    IAuditLog auditLog,
    ICurrentActor actor,
    SafetyGrantStore grants,
    IUserSessionRevoker sessions,
    TimeProvider timeProvider)
{
    public const string LockName = "ControlHome.Users";

    public async Task<UserDto[]> ListAsync(CancellationToken cancellationToken)
    {
        var users = await userManager.Users
            .AsNoTracking()
            .OrderBy(u => u.CreatedAtUtc)
            .ThenBy(u => u.UserName)
            .ToListAsync(cancellationToken);

        var roleByUser = new Dictionary<Guid, string>();
        foreach (var role in Roles.All)
        {
            foreach (var member in await userManager.GetUsersInRoleAsync(role))
            {
                roleByUser.TryAdd(member.Id, role); // Roles.All is most privileged first.
            }
        }

        return users.Select(u => ToDto(u, roleByUser.GetValueOrDefault(u.Id, Roles.Viewer))).ToArray();
    }

    public Task<Result<UserDto>> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Chosen before the unit runs, so a commit the client saw fail can be recognized (verifySucceeded).
        var id = Guid.CreateVersion7();
        return exclusive.RunAsync<UserDto>(
            LockName,
            async token =>
            {
                if (await DenyUnlessActiveAdminAsync() is { } denied)
                {
                    return denied;
                }

                if (await userManager.FindByNameAsync(request.UserName) is not null)
                {
                    return DuplicateUserName();
                }

                var user = new AppUser
                {
                    Id = id,
                    UserName = request.UserName,
                    DisplayName = request.DisplayName.Trim(),
                    Locale = await db.Homes.AsNoTracking().Select(h => h.Locale).FirstOrDefaultAsync(token) ?? Locales.English,
                    Theme = Themes.System,
                    CreatedAtUtc = timeProvider.GetUtcNow(),
                };

                var result = await userManager.CreateAsync(user, request.Password);
                if (!result.Succeeded)
                {
                    return result.ToAppError("password");
                }

                result = await userManager.AddToRoleAsync(user, request.Role);
                if (!result.Succeeded)
                {
                    return result.ToAppError("role");
                }

                await auditLog.WriteAsync(
                    new AuditEvent(AuditActions.UserCreated, AuthService.UserTarget, IdOf(user), new { user.UserName, request.Role }),
                    token);
                return ToDto(user, request.Role);
            },
            token => userManager.ExistsAsync(id, token),
            cancellationToken);
    }

    /// <summary>Changes display name, role and disabled state; 409 <c>users.last_admin</c> if no active Admin would remain.</summary>
    public async Task<Result<UserDto>> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var refreshOwnSession = false;
        var revoke = false;
        AppUser? updatedUser = null;
        var result = await exclusive.RunAsync<UserDto>(
            LockName,
            async token =>
            {
                if (await DenyUnlessActiveAdminAsync() is { } denied)
                {
                    return denied;
                }

                var user = await FindAsync(id);
                if (user is null)
                {
                    return NotFound();
                }

                var currentRole = await PrimaryRoleAsync(user);
                var wasActiveAdmin = currentRole == Roles.Admin && !user.IsDisabled;
                var staysActiveAdmin = request.Role == Roles.Admin && !request.IsDisabled;
                if (wasActiveAdmin && !staysActiveAdmin && await CountOtherActiveAdminsAsync(user.Id) == 0)
                {
                    return LastAdmin();
                }

                var changed = new List<string>();
                var displayName = request.DisplayName.Trim();
                if (!string.Equals(user.DisplayName, displayName, StringComparison.Ordinal))
                {
                    user.DisplayName = displayName;
                    changed.Add("displayName");
                }

                var disabledChanged = user.IsDisabled != request.IsDisabled;
                if (disabledChanged)
                {
                    user.IsDisabled = request.IsDisabled;
                    changed.Add("isDisabled");
                }

                if (changed.Count > 0)
                {
                    var updated = await userManager.UpdateAsync(user);
                    if (!updated.Succeeded)
                    {
                        return updated.ToAppError("displayName");
                    }
                }

                var roleChanged = !string.Equals(currentRole, request.Role, StringComparison.Ordinal);
                if (roleChanged)
                {
                    var removed = await userManager.RemoveFromRolesAsync(user, await userManager.GetRolesAsync(user));
                    var added = removed.Succeeded ? await userManager.AddToRoleAsync(user, request.Role) : removed;
                    if (!added.Succeeded)
                    {
                        return added.ToAppError("role");
                    }

                    changed.Add("role");
                }

                if (roleChanged || disabledChanged)
                {
                    // Ends the user's existing sessions at the next security-stamp check (at most a minute), and their Admin
                    // access at once (the Admin policy compares the stamp).
                    var stamped = await userManager.UpdateSecurityStampAsync(user);
                    if (!stamped.Succeeded)
                    {
                        return stamped.ToAppError("role");
                    }
                }

                if (changed.Count > 0)
                {
                    await auditLog.WriteAsync(
                        new AuditEvent(
                            AuditActions.UserUpdated,
                            AuthService.UserTarget,
                            IdOf(user),
                            new { user.UserName, Fields = changed, request.Role, request.IsDisabled }),
                        token);
                }

                refreshOwnSession = (roleChanged || disabledChanged) && !user.IsDisabled && user.Id == actor.UserId;
                revoke = roleChanged || disabledChanged;
                updatedUser = user;
                return ToDto(user, request.Role);
            },
            token => userManager.IsStoredAsync(updatedUser, token),
            cancellationToken);

        if (result.IsSuccess && refreshOwnSession && updatedUser is not null)
        {
            await session.RefreshAsync(updatedUser);
        }

        if (result.IsSuccess && revoke)
        {
            // Their security stamp changed: close their live hub connections now (HTTP requests re-check the stamp).
            await sessions.RevokeAsync(id, CancellationToken.None);
        }

        return result;
    }

    /// <summary>
    /// Sets a new password and clears any sign-in lockout and the safety PIN (the user sets a new one with the new password)
    /// in one write; the new security stamp ends the user's other sessions (the caller's own session is re-issued when they
    /// reset their own password).
    /// </summary>
    public async Task<Result> ResetPasswordAsync(Guid id, ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        AppUser? resetUser = null;
        var result = await exclusive.RunAsync<bool>(
            LockName,
            async token =>
            {
                if (await DenyUnlessActiveAdminAsync() is { } denied)
                {
                    return denied;
                }

                resetUser = await FindAsync(id);
                if (resetUser is null)
                {
                    return NotFound();
                }

                // Cleared first, so ResetPasswordAsync saves the counters and the PIN together with the new password and stamp.
                AuthService.ClearLockout(resetUser);
                resetUser.SafetyPinHash = null;
                var resetToken = await userManager.GeneratePasswordResetTokenAsync(resetUser);
                var changed = await userManager.ResetPasswordAsync(resetUser, resetToken, request.NewPassword);
                if (!changed.Succeeded)
                {
                    return changed.ToAppError("newPassword");
                }

                await auditLog.WriteAsync(
                    new AuditEvent(AuditActions.UserPasswordReset, AuthService.UserTarget, IdOf(resetUser), new { resetUser.UserName }),
                    token);
                return true;
            },
            token => userManager.IsStoredAsync(resetUser, token),
            cancellationToken);

        if (result.IsFailure)
        {
            return result.Error;
        }

        if (resetUser is not null && resetUser.Id == actor.UserId)
        {
            await session.RefreshAsync(resetUser);
        }

        grants.Forget(id);
        await sessions.RevokeAsync(id, CancellationToken.None);
        return Result.Success();
    }

    /// <summary>
    /// Clears the user's safety PIN (forgotten, or set by someone else on their session) and any safety grant or lockout; the
    /// user sets a new one with their account password. Audited <c>safety.pin.reset</c>; 404 <c>users.not_found</c>.
    /// </summary>
    public async Task<Result> ResetSafetyPinAsync(Guid id, CancellationToken cancellationToken)
    {
        AppUser? resetUser = null;
        var result = await exclusive.RunAsync<bool>(
            LockName,
            async token =>
            {
                if (await DenyUnlessActiveAdminAsync() is { } denied)
                {
                    return denied;
                }

                resetUser = await FindAsync(id);
                if (resetUser is null)
                {
                    return NotFound();
                }

                var hadPin = resetUser.SafetyPinHash is not null;
                if (hadPin)
                {
                    resetUser.SafetyPinHash = null;
                    var updated = await userManager.UpdateAsync(resetUser);
                    if (!updated.Succeeded)
                    {
                        return updated.ToAppError("id");
                    }
                }

                await auditLog.WriteAsync(
                    new AuditEvent(AuditActions.SafetyPinReset, AuthService.UserTarget, IdOf(resetUser), new { resetUser.UserName, HadPin = hadPin }),
                    token);
                return true;
            },
            token => userManager.IsStoredAsync(resetUser, token),
            cancellationToken);

        if (result.IsFailure)
        {
            return result.Error;
        }

        grants.Forget(id);
        return Result.Success();
    }

    /// <summary>Deletes the account; never yourself (<c>users.cannot_delete_self</c>), never the last active Admin.</summary>
    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await exclusive.RunAsync<bool>(
            LockName,
            async token =>
            {
                if (await DenyUnlessActiveAdminAsync() is { } denied)
                {
                    return denied;
                }

                var user = await FindAsync(id);
                if (user is null)
                {
                    return NotFound();
                }

                if (user.Id == actor.UserId)
                {
                    return AppError.Conflict(ErrorCodes.UsersCannotDeleteSelf, "You cannot delete your own account.");
                }

                // Defence in depth: with the caller re-checked above, another active Admin (the caller) always remains.
                var isActiveAdmin = !user.IsDisabled && await userManager.IsInRoleAsync(user, Roles.Admin);
                if (isActiveAdmin && await CountOtherActiveAdminsAsync(user.Id) == 0)
                {
                    return LastAdmin();
                }

                var deleted = await userManager.DeleteAsync(user);
                if (!deleted.Succeeded)
                {
                    return deleted.ToAppError("id");
                }

                await auditLog.WriteAsync(
                    new AuditEvent(AuditActions.UserDeleted, AuthService.UserTarget, IdOf(user), new { user.UserName }),
                    token);
                return true;
            },
            async token => !await userManager.ExistsAsync(id, token),
            cancellationToken);

        if (result.IsFailure)
        {
            return Result.Failure(result.Error);
        }

        grants.Forget(id);
        await sessions.RevokeAsync(id, CancellationToken.None);
        return Result.Success();
    }

    private static UserDto ToDto(AppUser user, string role) =>
        new(user.Id, user.UserName ?? string.Empty, user.DisplayName, role, user.IsDisabled, user.CreatedAtUtc, user.LastLoginAtUtc);

    private static string IdOf(AppUser user) => user.Id.ToString("D", CultureInfo.InvariantCulture);

    private static AppError NotFound() => AppError.NotFound(ErrorCodes.UsersNotFound, "No such user.");

    private static AppError DuplicateUserName() =>
        AppError.Conflict(ErrorCodes.UsersDuplicateUserName, "That user name is already taken.");

    private static AppError LastAdmin() =>
        AppError.Conflict(ErrorCodes.UsersLastAdmin, "At least one active Admin must remain.");

    private Task<AppUser?> FindAsync(Guid id) => userManager.FindByIdAsync(id.ToString("D", CultureInfo.InvariantCulture));

    /// <summary>
    /// Null when the caller is, in the database and right now (inside the lock), an existing, enabled Admin; otherwise 403
    /// <c>auth.forbidden</c>.
    /// </summary>
    private async Task<AppError?> DenyUnlessActiveAdminAsync()
    {
        if (actor.UserId is { } actorId
            && await FindAsync(actorId) is { IsDisabled: false } caller
            && await userManager.IsInRoleAsync(caller, Roles.Admin))
        {
            return null;
        }

        return AppError.Forbidden(ErrorCodes.AuthForbidden, "You are not allowed to do this.");
    }

    private async Task<string> PrimaryRoleAsync(AppUser user)
    {
        var roles = await userManager.GetRolesAsync(user);
        return Roles.All.FirstOrDefault(r => roles.Contains(r, StringComparer.Ordinal)) ?? Roles.Viewer;
    }

    private async Task<int> CountOtherActiveAdminsAsync(Guid excludedUserId) =>
        (await userManager.GetUsersInRoleAsync(Roles.Admin)).Count(u => u.Id != excludedUserId && !u.IsDisabled);
}

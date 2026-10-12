using System.Globalization;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Auditing;
using ControlHome.Application.Auth;
using ControlHome.Application.Common;
using ControlHome.Application.Identity;
using ControlHome.Domain.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Application.Users;

/// <summary>
/// Household accounts, managed by an Admin. Every change runs under the application lock <see cref="LockName"/>, so the
/// last-active-Admin rule (<c>users.last_admin</c>) holds even when two admins act at the same time. Disabling a user or
/// changing their role updates their security stamp, which ends their existing sessions at the next stamp check.
/// </summary>
public sealed class UserAdminService(
    UserManager<AppUser> userManager,
    IExclusiveTransaction exclusive,
    IControlHomeDb db,
    ISessionSignIn session,
    IAuditLog auditLog,
    ICurrentActor actor,
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
        return exclusive.RunAsync<UserDto>(LockName, async token =>
        {
            if (await userManager.FindByNameAsync(request.UserName) is not null)
            {
                return Result<UserDto>.Failure(DuplicateUserName());
            }

            var user = new AppUser
            {
                Id = Guid.CreateVersion7(),
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
        }, cancellationToken);
    }

    /// <summary>Changes display name, role and disabled state; 409 <c>users.last_admin</c> if no active Admin would remain.</summary>
    public async Task<Result<UserDto>> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var refreshOwnSession = false;
        AppUser? updatedUser = null;
        var result = await exclusive.RunAsync<UserDto>(LockName, async token =>
        {
            var user = await FindAsync(id);
            if (user is null)
            {
                return Result<UserDto>.Failure(NotFound());
            }

            var currentRole = await PrimaryRoleAsync(user);
            var wasActiveAdmin = currentRole == Roles.Admin && !user.IsDisabled;
            var staysActiveAdmin = request.Role == Roles.Admin && !request.IsDisabled;
            if (wasActiveAdmin && !staysActiveAdmin && await CountOtherActiveAdminsAsync(user.Id) == 0)
            {
                return Result<UserDto>.Failure(LastAdmin());
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
                // Ends the user's existing sessions at the next security-stamp check (at most a minute).
                await userManager.UpdateSecurityStampAsync(user);
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
            updatedUser = user;
            return ToDto(user, request.Role);
        }, cancellationToken);

        if (result.IsSuccess && refreshOwnSession && updatedUser is not null)
        {
            await session.RefreshAsync(updatedUser);
        }

        return result;
    }

    /// <summary>Sets a new password (ending the user's other sessions) and clears any sign-in lockout.</summary>
    public async Task<Result> ResetPasswordAsync(Guid id, ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await FindAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var result = await userManager.ResetPasswordAsync(user, token, request.NewPassword);
        if (!result.Succeeded)
        {
            return result.ToAppError("newPassword");
        }

        AuthService.ClearLockout(user);
        await userManager.UpdateAsync(user);
        await auditLog.WriteAsync(
            new AuditEvent(AuditActions.UserPasswordReset, AuthService.UserTarget, IdOf(user), new { user.UserName }),
            cancellationToken);

        if (user.Id == actor.UserId)
        {
            await session.RefreshAsync(user);
        }

        return Result.Success();
    }

    /// <summary>Deletes the account; never yourself (<c>users.cannot_delete_self</c>), never the last active Admin.</summary>
    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await exclusive.RunAsync<bool>(LockName, async token =>
        {
            var user = await FindAsync(id);
            if (user is null)
            {
                return Result<bool>.Failure(NotFound());
            }

            if (user.Id == actor.UserId)
            {
                return Result<bool>.Failure(
                    AppError.Conflict(ErrorCodes.UsersCannotDeleteSelf, "You cannot delete your own account."));
            }

            var isActiveAdmin = !user.IsDisabled && await userManager.IsInRoleAsync(user, Roles.Admin);
            if (isActiveAdmin && await CountOtherActiveAdminsAsync(user.Id) == 0)
            {
                return Result<bool>.Failure(LastAdmin());
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
        }, cancellationToken);

        return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
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

    private async Task<string> PrimaryRoleAsync(AppUser user)
    {
        var roles = await userManager.GetRolesAsync(user);
        return Roles.All.FirstOrDefault(r => roles.Contains(r, StringComparer.Ordinal)) ?? Roles.Viewer;
    }

    private async Task<int> CountOtherActiveAdminsAsync(Guid excludedUserId) =>
        (await userManager.GetUsersInRoleAsync(Roles.Admin)).Count(u => u.Id != excludedUserId && !u.IsDisabled);
}

using System.Globalization;
using ControlHome.Application.Auditing;
using ControlHome.Application.Common;
using ControlHome.Application.Identity;
using ControlHome.Domain.Security;
using Microsoft.AspNetCore.Identity;

namespace ControlHome.Application.Auth;

/// <summary>A one-time password-reset link for an administrator (printed by the <c>reset-admin</c> command).</summary>
/// <param name="Path">Relative URL of the SPA page that consumes it: <c>/reset-password?user=…&amp;token=…</c>.</param>
public sealed record AdminResetLink(string UserName, string Path, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Recovery for a locked-out or forgotten administrator account: issues an Identity password-reset token (valid for
/// <see cref="TokenLifetime"/>, single use because consuming it changes the security stamp). Audited as
/// <c>auth.admin_reset</c>.
/// </summary>
public sealed class AdminPasswordResetService(UserManager<AppUser> userManager, IAuditLog auditLog, TimeProvider timeProvider)
{
    public const string ResetPagePath = "/reset-password";

    /// <summary>How long a reset token stays valid (Identity's <c>DataProtectionTokenProviderOptions.TokenLifespan</c>).</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);

    public async Task<Result<AdminResetLink>> CreateResetLinkAsync(string userName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);

        var user = await userManager.FindByNameAsync(userName);
        if (user is null)
        {
            return AppError.NotFound(ErrorCodes.UsersNotFound, $"There is no user named '{userName}'.");
        }

        if (!await userManager.IsInRoleAsync(user, Roles.Admin))
        {
            return AppError.Forbidden(
                ErrorCodes.AuthForbidden,
                $"'{user.UserName}' is not an administrator; an administrator can reset it from the Users page.");
        }

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        await auditLog.WriteAsync(
            new AuditEvent(
                AuditActions.AdminReset,
                AuthService.UserTarget,
                user.Id.ToString("D", CultureInfo.InvariantCulture),
                new { user.UserName, Step = "token_issued" }),
            cancellationToken);

        var path = $"{ResetPagePath}?user={Uri.EscapeDataString(user.UserName!)}&token={Uri.EscapeDataString(token)}";
        return new AdminResetLink(user.UserName!, path, timeProvider.GetUtcNow() + TokenLifetime);
    }
}

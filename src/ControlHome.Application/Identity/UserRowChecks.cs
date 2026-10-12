using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Application.Identity;

/// <summary>
/// Database checks for the <c>verifySucceeded</c> callback of an <see cref="Abstractions.IExclusiveTransaction"/> unit that
/// writes user rows: after a transient error while committing, did that unit's writes reach the database?
/// </summary>
internal static class UserRowChecks
{
    /// <summary>
    /// Whether the stored row of <paramref name="user"/> (the unit's tracked entity) carries the entity's current concurrency
    /// stamp. Identity sets a new stamp on every update, so this is true exactly when the unit's last write to the row was
    /// committed, or when the unit did not write the row (or loaded no user) and running it again would change nothing.
    /// </summary>
    public static async Task<bool> IsStoredAsync(this UserManager<AppUser> userManager, AppUser? user, CancellationToken cancellationToken)
    {
        if (user is null)
        {
            return true;
        }

        var id = user.Id;
        var stamp = user.ConcurrencyStamp;
        return await userManager.Users.AsNoTracking().AnyAsync(u => u.Id == id && u.ConcurrencyStamp == stamp, cancellationToken);
    }

    /// <summary>Whether a user row with <paramref name="id"/> exists (e.g. one the unit created with an id chosen beforehand).</summary>
    public static Task<bool> ExistsAsync(this UserManager<AppUser> userManager, Guid id, CancellationToken cancellationToken) =>
        userManager.Users.AsNoTracking().AnyAsync(u => u.Id == id, cancellationToken);
}

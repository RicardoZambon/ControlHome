using System.Security.Claims;
using ControlHome.Application.Identity;
using ControlHome.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Api.Hosting;

/// <summary>
/// Part of the <see cref="AuthorizationPolicies.Admin"/> policy: the signed-in user is, in the database right now, an
/// existing, enabled Admin whose security stamp still matches the session's. The cookie's role claim alone can be up to a
/// minute old (the stamp is re-validated only every minute), and a demoted or disabled admin must lose Admin access at once,
/// not keep it long enough to undo the change.
/// </summary>
internal sealed class ActiveAdminRequirement : IAuthorizationRequirement;

/// <summary>Checks <see cref="ActiveAdminRequirement"/> against the user row (one query for the user, one for the role).</summary>
internal sealed class ActiveAdminHandler(UserManager<AppUser> userManager) : AuthorizationHandler<ActiveAdminRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ActiveAdminRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true || !Guid.TryParse(userManager.GetUserId(context.User), out var id))
        {
            return;
        }

        var user = await userManager.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id);
        var sessionStamp = context.User.FindFirstValue(userManager.Options.ClaimsIdentity.SecurityStampClaimType);
        if (user is { IsDisabled: false }
            && string.Equals(user.SecurityStamp, sessionStamp, StringComparison.Ordinal)
            && await userManager.IsInRoleAsync(user, Roles.Admin))
        {
            context.Succeed(requirement);
        }
    }
}

using System.Security.Claims;
using ControlHome.Application.Security;

namespace ControlHome.Api.Hosting;

/// <summary>The <see cref="CommandPrincipal"/> of a signed-in request.</summary>
internal static class CommandPrincipals
{
    /// <exception cref="InvalidOperationException">The request is not signed in (endpoints require authorization first).</exception>
    public static CommandPrincipal FromUser(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
        {
            throw new InvalidOperationException("The request has no signed-in user.");
        }

        var roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).Distinct(StringComparer.Ordinal).ToList();
        return CommandPrincipal.User(id, user.Identity?.Name, roles);
    }
}

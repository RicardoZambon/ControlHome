using ControlHome.Application.Identity;
using ControlHome.Domain.Security;
using Microsoft.AspNetCore.Identity;

namespace ControlHome.Application.Auth;

public static class MeMapping
{
    /// <summary>The user as <see cref="MeDto"/>, roles most privileged first.</summary>
    public static async Task<MeDto> ToMeDtoAsync(this UserManager<AppUser> userManager, AppUser user)
    {
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(user);

        var roles = await userManager.GetRolesAsync(user);
        var ordered = Roles.All.Where(r => roles.Contains(r, StringComparer.Ordinal)).ToArray();
        return new MeDto(user.Id, user.UserName ?? string.Empty, user.DisplayName, ordered, user.Locale, user.Theme);
    }
}

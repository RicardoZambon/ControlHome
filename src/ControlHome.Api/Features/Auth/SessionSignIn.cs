using ControlHome.Application.Auth;
using ControlHome.Application.Identity;
using Microsoft.AspNetCore.Identity;

namespace ControlHome.Api.Features.Auth;

/// <summary><see cref="ISessionSignIn"/> with the Identity application cookie (<c>ControlHome.Session</c>).</summary>
internal sealed class SessionSignIn(SignInManager<AppUser> signInManager) : ISessionSignIn
{
    public async Task SignInAsync(AppUser user, bool persistent)
    {
        await signInManager.SignInAsync(user, persistent);

        // The rest of this request (e.g. its audit entry) acts as the user that just signed in.
        signInManager.Context.User = await signInManager.CreateUserPrincipalAsync(user);
    }

    public Task RefreshAsync(AppUser user) => signInManager.RefreshSignInAsync(user);

    public Task SignOutAsync() => signInManager.SignOutAsync();
}

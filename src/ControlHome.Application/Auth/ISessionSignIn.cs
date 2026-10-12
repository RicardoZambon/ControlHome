using ControlHome.Application.Identity;

namespace ControlHome.Application.Auth;

/// <summary>The browser session (the Api implements it with the Identity cookie).</summary>
public interface ISessionSignIn
{
    /// <summary>Issues the session cookie for <paramref name="user"/>; the current request then acts as that user.</summary>
    /// <param name="persistent">Keep the cookie across browser restarts.</param>
    Task SignInAsync(AppUser user, bool persistent);

    /// <summary>Re-issues the current user's cookie (e.g. after their security stamp changed), keeping its persistence.</summary>
    Task RefreshAsync(AppUser user);

    /// <summary>Removes the session cookie.</summary>
    Task SignOutAsync();
}

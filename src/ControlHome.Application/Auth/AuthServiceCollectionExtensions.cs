using ControlHome.Application.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ControlHome.Application.Auth;

public static class AuthServiceCollectionExtensions
{
    /// <summary>
    /// Registers the auth feature's application services (sign-in, own profile, admin password reset) and, unless the host
    /// has live connections of its own (the Api's hub), a do-nothing <see cref="IUserSessionRevoker"/>.
    /// </summary>
    public static IServiceCollection AddAuth(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SafetyGrantStore>();
        services.TryAddSingleton<IUserSessionRevoker>(NoUserSessions.Instance);
        services.AddScoped<AuthService>();
        services.AddScoped<AdminPasswordResetService>();
        services.AddSingleton<UnknownUserLockouts>();
        return services;
    }
}

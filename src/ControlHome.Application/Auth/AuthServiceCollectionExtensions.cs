using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Application.Auth;

public static class AuthServiceCollectionExtensions
{
    /// <summary>Registers the auth feature's application services (sign-in, own profile, admin password reset).</summary>
    public static IServiceCollection AddAuth(this IServiceCollection services)
    {
        services.AddScoped<AuthService>();
        services.AddScoped<AdminPasswordResetService>();
        services.AddSingleton<UnknownUserLockouts>();
        return services;
    }
}

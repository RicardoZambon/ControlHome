using ControlHome.Application.Auth;
using ControlHome.Application.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ControlHome.Application.Users;

public static class UsersServiceCollectionExtensions
{
    /// <summary>Registers the user administration feature's application services.</summary>
    public static IServiceCollection AddUsers(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SafetyGrantStore>();
        services.TryAddSingleton<IUserSessionRevoker>(NoUserSessions.Instance);
        services.AddScoped<UserAdminService>();
        return services;
    }
}

using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Application.Users;

public static class UsersServiceCollectionExtensions
{
    /// <summary>Registers the user administration feature's application services.</summary>
    public static IServiceCollection AddUsers(this IServiceCollection services)
    {
        services.AddScoped<UserAdminService>();
        return services;
    }
}

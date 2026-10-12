using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Application.Homes;

public static class HomesServiceCollectionExtensions
{
    /// <summary>Registers the home settings feature's application services.</summary>
    public static IServiceCollection AddHomes(this IServiceCollection services)
    {
        services.AddScoped<HomeService>();
        return services;
    }
}

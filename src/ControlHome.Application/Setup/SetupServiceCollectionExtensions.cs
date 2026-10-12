using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Application.Setup;

public static class SetupServiceCollectionExtensions
{
    /// <summary>Registers the setup feature's application services.</summary>
    public static IServiceCollection AddSetup(this IServiceCollection services)
    {
        services.AddScoped<SetupService>();
        return services;
    }
}

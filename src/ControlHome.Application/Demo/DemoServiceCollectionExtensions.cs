using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Application.Demo;

public static class DemoServiceCollectionExtensions
{
    /// <summary>Registers the demo home loader (needs the devices, integrations and placements features).</summary>
    public static IServiceCollection AddDemoHome(this IServiceCollection services)
    {
        services.AddScoped<DemoHomeService>();
        return services;
    }
}

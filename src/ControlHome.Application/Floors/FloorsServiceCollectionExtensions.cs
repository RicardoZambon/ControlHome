using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Application.Floors;

public static class FloorsServiceCollectionExtensions
{
    /// <summary>Registers the floors and rooms feature's application services.</summary>
    public static IServiceCollection AddFloors(this IServiceCollection services)
    {
        services.AddScoped<FloorService>();
        return services;
    }
}

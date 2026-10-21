using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Application.Placements;

public static class PlacementsServiceCollectionExtensions
{
    /// <summary>Registers the floor-plan placements feature's application services.</summary>
    public static IServiceCollection AddPlacements(this IServiceCollection services)
    {
        services.AddScoped<PlacementService>();
        return services;
    }
}

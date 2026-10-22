using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Application.Dashboard;

public static class DashboardServiceCollectionExtensions
{
    /// <summary>Registers the dashboard summary (needs the devices feature's readers).</summary>
    public static IServiceCollection AddDashboard(this IServiceCollection services)
    {
        services.AddScoped<DashboardService>();
        return services;
    }
}

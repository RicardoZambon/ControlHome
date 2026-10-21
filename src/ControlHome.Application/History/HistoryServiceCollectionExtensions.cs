using ControlHome.Application.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ControlHome.Application.History;

public static class HistoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the history feature's application services: <see cref="HistoryQueryService"/> and
    /// <see cref="RetentionSettingsService"/> — and, unless the device runtime registered its own, the app clock as the
    /// <see cref="IRuntimeClock"/>.
    /// </summary>
    public static IServiceCollection AddHistory(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IRuntimeClock>(sp => new AppRuntimeClock(sp.GetRequiredService<TimeProvider>()));
        services.AddScoped<HistoryQueryService>();
        services.AddScoped<RetentionSettingsService>();
        return services;
    }
}

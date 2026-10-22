using ControlHome.Infrastructure.Devices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ControlHome.Infrastructure.History;

public static class HistoryRuntimeServiceCollectionExtensions
{
    /// <summary>
    /// The history rollups and the retention job with its built-in targets (part of the device runtime; nothing runs until
    /// <see cref="AddHistoryMaintenanceHost"/>'s host does).
    /// </summary>
    public static IServiceCollection AddHistoryRuntime(this IServiceCollection services)
    {
        services.AddOptions<HistoryOptions>().BindConfiguration(HistoryOptions.SectionName);
        services.TryAddSingleton<IPendingHistory>(sp => sp.GetRequiredService<StateWriteBehind>());
        services.TryAddSingleton<HistoryRollup>();
        services.TryAddSingleton<RetentionPurge>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRetentionPurgeTarget, StateLogPurgeTarget>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRetentionPurgeTarget, ShortTermPurgeTarget>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRetentionPurgeTarget, DeviceEventPurgeTarget>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRetentionPurgeTarget, AuditLogPurgeTarget>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRetentionPurgeTarget, OrphanedHistoryPurgeTarget>());
        return services;
    }

    /// <summary>Runs the rollups and the retention job with the host (not during build-time OpenAPI generation).</summary>
    public static IServiceCollection AddHistoryMaintenanceHost(this IServiceCollection services)
    {
        services.AddHostedService<HistoryMaintenanceHost>();
        return services;
    }
}

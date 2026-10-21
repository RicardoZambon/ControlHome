using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ControlHome.Application.HomeBackup;

public static class BackupServiceCollectionExtensions
{
    /// <summary>
    /// Registers backup export and import, and no automatic backups (Infrastructure's <c>AddBackupSchedule</c> replaces
    /// <see cref="IScheduledBackups"/> with the daily export).
    /// </summary>
    public static IServiceCollection AddBackup(this IServiceCollection services)
    {
        services.AddScoped<BackupService>();
        services.TryAddSingleton<IScheduledBackups>(NoScheduledBackups.Instance);
        return services;
    }
}

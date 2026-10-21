using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Application.HomeBackup;

public static class BackupServiceCollectionExtensions
{
    /// <summary>Registers backup export and import (the daily export is Infrastructure's <c>AddBackupSchedule</c>).</summary>
    public static IServiceCollection AddBackup(this IServiceCollection services)
    {
        services.AddScoped<BackupService>();
        return services;
    }
}

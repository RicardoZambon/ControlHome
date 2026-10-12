using ControlHome.Application.Abstractions;
using ControlHome.Application.Auditing;
using ControlHome.Application.Files;
using ControlHome.Infrastructure.Auditing;
using ControlHome.Infrastructure.Configuration;
using ControlHome.Infrastructure.Files;
using ControlHome.Infrastructure.Persistence;
using ControlHome.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ControlHome.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Options, data directory, the database connection (configuration, then the setup wizard's encrypted
    /// <c>config/database.json</c>), DbContext (+ <see cref="IControlHomeDb"/>), database readiness, the audit log and the
    /// image processor.
    /// Everything is resolved lazily: registering opens no connection and reads no file.
    /// The <see cref="DatabaseInitializer"/> is added separately by <see cref="AddDatabaseInitializer"/>.
    /// </summary>
    public static IServiceCollection AddControlHomeInfrastructure(this IServiceCollection services)
    {
        services.AddOptions<ControlHomeOptions>().BindConfiguration(ControlHomeOptions.SectionName);
        services.AddOptions<DatabaseOptions>().BindConfiguration(DatabaseOptions.SectionName);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<DataDirectory>();
        services.TryAddSingleton<DataProtectionCertificateStore>();
        services.TryAddSingleton<BootstrapDataProtection>();
        services.TryAddSingleton<DatabaseConfigFile>();
        services.TryAddSingleton<IDatabaseConnectionProvider, DatabaseConnectionProvider>();
        services.TryAddSingleton<ISqlServerProbe, SqlServerProbe>();
        services.TryAddSingleton<DatabaseReadiness>();
        services.TryAddSingleton<IDatabaseStatus, DatabaseStatus>();

        services.AddDbContext<ControlHomeDbContext>(ConfigureDbContext);
        services.AddScoped<IControlHomeDb>(sp => sp.GetRequiredService<ControlHomeDbContext>());
        services.AddScoped<IAuditLog, AuditLog>();
        services.AddScoped<DatabaseReset>();
        services.TryAddSingleton<IImageProcessor, SkiaImageProcessor>();

        return services;
    }

    /// <summary>Starts <see cref="DatabaseInitializer"/> with the host (not during build-time OpenAPI generation).</summary>
    public static IServiceCollection AddDatabaseInitializer(this IServiceCollection services)
    {
        services.AddHostedService<DatabaseInitializer>();
        return services;
    }

    private static void ConfigureDbContext(IServiceProvider services, DbContextOptionsBuilder options)
    {
        var connectionString = services.GetRequiredService<IDatabaseConnectionProvider>().GetConnectionString();
        if (connectionString is null)
        {
            options.UseSqlServer(ConfigureSqlServer);
        }
        else
        {
            options.UseSqlServer(connectionString, ConfigureSqlServer);
        }

        if (services.GetService<IHostEnvironment>()?.IsDevelopment() == true)
        {
            options.EnableSensitiveDataLogging();
        }
    }

    private static void ConfigureSqlServer(Microsoft.EntityFrameworkCore.Infrastructure.SqlServerDbContextOptionsBuilder sql)
    {
        // SQL Server 2025 is required: native json columns and 2025 semantics are always on.
        sql.UseCompatibilityLevel(170);
        sql.EnableRetryOnFailure();
        sql.MigrationsHistoryTable(ControlHomeDbContext.MigrationsHistoryTable, ControlHomeDbContext.AppSchema);
    }
}

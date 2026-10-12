using ControlHome.Application.Abstractions;
using ControlHome.Application.Setup;
using ControlHome.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ControlHome.Infrastructure.Setup;

public static class SetupInfrastructureExtensions
{
    /// <summary>
    /// The setup code (<see cref="ISetupCodeProvider"/>, singleton) and the exclusive transaction the setup completion runs
    /// in. Registering touches nothing; the code is generated when first resolved.
    /// </summary>
    public static IServiceCollection AddSetupInfrastructure(this IServiceCollection services)
    {
        services.AddOptions<SetupOptions>().BindConfiguration(SetupOptions.SectionName);
        services.TryAddSingleton<SetupCodeProvider>();
        services.TryAddSingleton<ISetupCodeProvider>(sp => sp.GetRequiredService<SetupCodeProvider>());
        services.TryAddScoped<IExclusiveTransaction, SqlExclusiveTransaction>();
        return services;
    }

    /// <summary>Logs / writes the setup code at startup while setup is incomplete (not during build-time OpenAPI generation).</summary>
    public static IServiceCollection AddSetupCodeAnnouncer(this IServiceCollection services)
    {
        services.AddHostedService<SetupCodeAnnouncer>();
        return services;
    }
}

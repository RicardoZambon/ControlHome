using ControlHome.Application.Devices;
using ControlHome.Application.Homes;
using ControlHome.Application.Runtime;
using ControlHome.Application.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ControlHome.Application.Integrations;

public static class IntegrationsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the integrations feature: the connector catalogue (from every registered <c>IConnectorFactory</c>) and
    /// <see cref="IntegrationService"/>. Without a device runtime (Infrastructure's <c>AddDeviceRuntime</c>) nothing starts.
    /// </summary>
    public static IServiceCollection AddIntegrations(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<KnownSecrets>();
        services.TryAddSingleton<ConnectorCatalog>();
        services.TryAddSingleton<IIntegrationRuntimeControl>(NoDeviceRuntime.Instance);
        services.TryAddSingleton<IRuntimeStatus>(NoDeviceRuntime.Instance);
        services.TryAddSingleton<IIntegrationSecretStores>(NoIntegrationSecretStores.Instance);
        services.TryAddSingleton<IRuntimeClock>(sp => new AppRuntimeClock(sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IntegrationFlowStore>();
        services.TryAddScoped<IHomeLanguage, HomeLanguage>();
        services.AddScoped<IntegrationService>();
        services.AddScoped<IntegrationFlowService>();
        return services;
    }
}

using ControlHome.Application.Devices;
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
        services.TryAddSingleton<ConnectorCatalog>();
        services.TryAddSingleton<IIntegrationRuntimeControl>(NoDeviceRuntime.Instance);
        services.AddScoped<IntegrationService>();
        return services;
    }
}

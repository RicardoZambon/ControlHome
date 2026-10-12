using ControlHome.Connectors.Simulated;
using ControlHome.Infrastructure.Devices;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ControlHome.Api.Hosting;

/// <summary>The connectors built into this app, registered statically (never loaded as plugins).</summary>
public static class ConnectorSetup
{
    public static IServiceCollection AddControlHomeConnectors(this IServiceCollection services)
    {
        // Simulated: dev, demo and E2E home. Default seed from Simulated:Seed (Simulated__Seed).
        services.AddOptions<SimulatedConnectorOptions>().BindConfiguration(SimulatedConnectorOptions.SectionName);
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<SimulatedConnectorOptions>>().Value);
        services.AddConnector<SimulatedConnectorFactory>();

        return services;
    }
}

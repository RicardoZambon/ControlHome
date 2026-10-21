using ControlHome.Connectors.Mqtt;
using ControlHome.Connectors.Simulated;
using ControlHome.Connectors.SmartThings;
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

        // MQTT: Zigbee2MQTT, Home Assistant MQTT discovery and Tasmota over one broker connection per integration.
        services.AddConnector<MqttConnectorFactory>();

        // SmartThings: the household's own OAuth-In app (or a dev token), adaptive polling of the cloud API per location.
        services.AddSmartThingsHttpClient();
        services.AddConnector<SmartThingsConnectorFactory>();

        return services;
    }
}

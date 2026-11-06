using ControlHome.Connectors.DahuaHttp;
using ControlHome.Connectors.LgThinQ;
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

        // LG ThinQ: appliances through the ThinQ Connect personal access token API (REST + outbound MQTT).
        services.AddConnector<LgThinQConnectorFactory>();
        // Intelbras / Dahua-family cameras, recorders and SS access terminals (CGI API, Digest auth). The XPE door-station
        // relay stays behind Connectors:DahuaHttp:XpeRelay (off) until tested on hardware.
        services.AddOptions<DahuaHttpConnectorOptions>().BindConfiguration(DahuaHttpConnectorOptions.SectionName);
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<DahuaHttpConnectorOptions>>().Value);
        services.AddConnector<DahuaHttpConnectorFactory>();
        // SmartThings: the household's own OAuth-In app (or a dev token), adaptive polling of the cloud API per location.
        services.AddSmartThingsHttpClient().AddConnector<SmartThingsConnectorFactory>();

        return services;
    }
}

using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Connectors.SmartThings;

/// <summary>DI registration of the SmartThings connector's HTTP client (the Api adds <c>AddConnector&lt;SmartThingsConnectorFactory&gt;()</c>).</summary>
public static class SmartThingsServiceCollectionExtensions
{
    /// <summary>The named <see cref="HttpClient"/> the connector uses (<see cref="IHttpClientFactory"/>).</summary>
    public const string HttpClientName = "ControlHome.SmartThings";

    /// <summary>
    /// Registers the connector's named HTTP client: no automatic redirects (a bearer token never follows one), pooled
    /// connections recycled every 5 minutes (DNS changes), and a 2-minute safety timeout — each request has its own
    /// shorter timeout on the connector's clock, and retries (idempotent GETs only) are the connector's.
    /// </summary>
    public static IServiceCollection AddSmartThingsHttpClient(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services
            .AddHttpClient(HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromMinutes(2);
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ControlHome", typeof(SmartThingsServiceCollectionExtensions).Assembly.GetName().Version?.ToString(3) ?? "0"));
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            });
        return services;
    }
}

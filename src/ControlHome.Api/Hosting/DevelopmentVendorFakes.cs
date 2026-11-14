using ControlHome.Connectors.SmartThings;

namespace ControlHome.Api.Hosting;

/// <summary>
/// Development only: points the SmartThings connector at a local fake cloud (<c>scripts/fake-smartthings/server.mjs</c>,
/// serving the connector's test fixtures) when <c>Development:SmartThingsApiUrl</c> is set to a loopback URL, so the real
/// app can be tried with Samsung appliances without a Samsung account. Never active outside the Development environment.
/// </summary>
public static class DevelopmentVendorFakes
{
    public const string SmartThingsApiUrlKey = "Development:SmartThingsApiUrl";

    public static WebApplicationBuilder AddDevelopmentVendorFakes(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!builder.Environment.IsDevelopment()
            || builder.Configuration[SmartThingsApiUrlKey] is not { Length: > 0 } value
            || !Uri.TryCreate(value, UriKind.Absolute, out var target)
            || !target.IsLoopback)
        {
            return builder;
        }

        builder.Services
            .AddHttpClient(SmartThingsServiceCollectionExtensions.HttpClientName)
            .AddHttpMessageHandler(() => new RedirectHandler(target));
        return builder;
    }

    /// <summary>Sends every SmartThings request to the fake instead, keeping its path and query.</summary>
    private sealed class RedirectHandler(Uri target) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is { } uri)
            {
                request.RequestUri = new UriBuilder(uri) { Scheme = target.Scheme, Host = target.Host, Port = target.Port }.Uri;
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}

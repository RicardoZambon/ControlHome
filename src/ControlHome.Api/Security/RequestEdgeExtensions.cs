using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ControlHome.Api.Security;

/// <summary>
/// The request edge: forwarded headers (trusted proxies only), security headers, the client network allowlist and the
/// cross-origin checks, in that order, before static files and routing.
/// </summary>
public static class RequestEdgeExtensions
{
    /// <summary>Binds <see cref="NetworkOptions"/> (validated at startup) and configures forwarded headers from it.</summary>
    public static IServiceCollection AddControlHomeRequestEdge(this IServiceCollection services)
    {
        services.AddOptions<NetworkOptions>()
            .Configure<IConfiguration>((options, configuration) => options.Bind(configuration.GetSection(NetworkOptions.SectionName)))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<NetworkOptions>, NetworkOptionsValidator>());

        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<NetworkOptions>>((forwarded, network) => ConfigureForwardedHeaders(forwarded, network.Value));

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<NetworkDeniedAuditThrottle>();
        services.TryAddSingleton(static provider => new ClientNetworkPolicy(
            IpNetworkParser.ParseAll(provider.GetRequiredService<IOptions<NetworkOptions>>().Value.AllowedClientNetworks)));

        return services;
    }

    /// <summary>Call right after the exception handler / status-code pages and before static files and routing.</summary>
    public static IApplicationBuilder UseControlHomeRequestEdge(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseForwardedHeaders();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseMiddleware<NetworkAllowlistMiddleware>();
        app.UseMiddleware<OriginValidationMiddleware>();
        return app;
    }

    /// <summary>
    /// <c>X-Forwarded-For</c>, <c>-Proto</c> and <c>-Host</c> (one hop). Without <see cref="NetworkOptions.TrustedProxyNetworks"/>
    /// the framework default applies (loopback proxies only); with it, exactly those networks are trusted.
    /// </summary>
    private static void ConfigureForwardedHeaders(ForwardedHeadersOptions forwarded, NetworkOptions network)
    {
        forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;

        var proxies = IpNetworkParser.ParseAll(network.TrustedProxyNetworks);
        if (proxies.Count == 0)
        {
            return;
        }

        forwarded.KnownProxies.Clear();
        forwarded.KnownIPNetworks.Clear();
        foreach (var proxy in proxies)
        {
            forwarded.KnownIPNetworks.Add(proxy);
        }
    }
}

using System.Text.Json.Serialization;
using ControlHome.Application.Auth;
using ControlHome.Application.Realtime;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ControlHome.Api.Realtime;

public static class RealtimeSetup
{
    /// <summary>
    /// SignalR (JSON protocol, camelCase, strict numbers), the hub publisher as the app's <see cref="IRealtimePublisher"/>,
    /// and the connection registry as its <see cref="IUserSessionRevoker"/> (with its periodic re-check).
    /// </summary>
    public static IServiceCollection AddControlHomeRealtime(this IServiceCollection services)
    {
        services.AddSignalR(options => options.EnableDetailedErrors = false)
            .AddJsonProtocol(options => options.PayloadSerializerOptions.NumberHandling = JsonNumberHandling.Strict);
        services.TryAddSingleton<HomeHubPublisher>();
        services.Replace(ServiceDescriptor.Singleton<IRealtimePublisher>(sp => sp.GetRequiredService<HomeHubPublisher>()));
        services.TryAddSingleton<HubConnectionRegistry>();
        services.Replace(ServiceDescriptor.Singleton<IUserSessionRevoker>(sp => sp.GetRequiredService<HubConnectionRegistry>()));
        services.AddHostedService<HubSessionValidator>();
        return services;
    }

    /// <summary>
    /// Maps <see cref="HomeHub"/> at <see cref="HomeHubMessages.Path"/> (signed-in users only); a connection also closes
    /// when its session cookie expires.
    /// </summary>
    public static IEndpointRouteBuilder MapHomeHub(this IEndpointRouteBuilder app)
    {
        app.MapHub<HomeHub>(HomeHubMessages.Path, options => options.CloseOnAuthenticationExpiration = true);
        return app;
    }
}

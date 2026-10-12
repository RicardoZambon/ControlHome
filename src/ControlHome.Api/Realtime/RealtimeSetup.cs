using System.Text.Json.Serialization;
using ControlHome.Application.Realtime;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ControlHome.Api.Realtime;

public static class RealtimeSetup
{
    /// <summary>SignalR (JSON protocol, camelCase, strict numbers) and the hub publisher as the app's <see cref="IRealtimePublisher"/>.</summary>
    public static IServiceCollection AddControlHomeRealtime(this IServiceCollection services)
    {
        services.AddSignalR(options => options.EnableDetailedErrors = false)
            .AddJsonProtocol(options => options.PayloadSerializerOptions.NumberHandling = JsonNumberHandling.Strict);
        services.TryAddSingleton<HomeHubPublisher>();
        services.Replace(ServiceDescriptor.Singleton<IRealtimePublisher>(sp => sp.GetRequiredService<HomeHubPublisher>()));
        return services;
    }

    /// <summary>Maps <see cref="HomeHub"/> at <see cref="HomeHubMessages.Path"/> (signed-in users only).</summary>
    public static IEndpointRouteBuilder MapHomeHub(this IEndpointRouteBuilder app)
    {
        app.MapHub<HomeHub>(HomeHubMessages.Path);
        return app;
    }
}

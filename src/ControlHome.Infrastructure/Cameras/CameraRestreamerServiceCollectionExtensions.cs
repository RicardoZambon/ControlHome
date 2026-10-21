using ControlHome.Application.Devices;
using ControlHome.Application.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlHome.Infrastructure.Cameras;

public static class CameraRestreamerServiceCollectionExtensions
{
    /// <summary>The named HTTP client of the go2rtc API (no proxy, no redirects, no request logging: URLs carry camera credentials).</summary>
    public const string HttpClientName = "go2rtc";

    /// <summary>
    /// The go2rtc restreamer (<see cref="Go2RtcRestreamer"/>, options from <see cref="Go2RtcOptions.SectionName"/>) as the
    /// app's <see cref="ICameraRestreamer"/>. Registering opens no connection; live view stays off until it is configured.
    /// </summary>
    public static IServiceCollection AddCameraRestreamer(this IServiceCollection services)
    {
        services.AddOptions<Go2RtcOptions>().BindConfiguration(Go2RtcOptions.SectionName);
        services.AddHttpClient(HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                UseCookies = false,
            })
            .RemoveAllLoggers();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<KnownSecrets>();
        services.TryAddSingleton(static sp =>
        {
            var clients = sp.GetRequiredService<IHttpClientFactory>();
            return new Go2RtcRestreamer(
                sp.GetRequiredService<IOptions<Go2RtcOptions>>(),
                () => clients.CreateClient(HttpClientName),
                sp.GetRequiredService<KnownSecrets>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<Go2RtcRestreamer>>());
        });
        services.Replace(ServiceDescriptor.Singleton<ICameraRestreamer>(static sp => sp.GetRequiredService<Go2RtcRestreamer>()));
        return services;
    }
}

using System.Net;
using ControlHome.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Api.IntegrationTests.RequestEdge;

/// <summary>
/// The API (with a database) where a request can pretend to come from any client address: the header
/// <see cref="RemoteIpStartupFilter.HeaderName"/> sets <c>Connection.RemoteIpAddress</c> before anything else runs, as
/// Kestrel would for a real socket (TestServer leaves it null).
/// </summary>
public class RequestEdgeApiFactory(SqlServerFixture sql) : ControlHomeApiFactory(sql)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, RemoteIpStartupFilter>());
    }
}

/// <summary><see cref="RequestEdgeApiFactory"/> behind a configured proxy subnet (<c>Network:TrustedProxyNetworks</c>).</summary>
public sealed class TrustedProxyApiFactory(SqlServerFixture sql) : RequestEdgeApiFactory(sql)
{
    public const string ProxyNetwork = "172.30.0.0/16";

    protected override void ConfigureSettings(IDictionary<string, string?> settings) =>
        settings["Network:TrustedProxyNetworks"] = ProxyNetwork;
}

/// <summary>The API in Development without a database (Scalar is mapped only in Development).</summary>
public sealed class DevelopmentApiFactory(SqlServerFixture sql) : ControlHomeApiFactory(sql)
{
    protected override bool UseDatabase => false;

    protected override string EnvironmentName => "Development";
}

/// <summary>Outermost middleware: <c>X-Test-Remote-IP: 8.8.8.8</c> becomes the connection's remote address.</summary>
internal sealed class RemoteIpStartupFilter : IStartupFilter
{
    public const string HeaderName = "X-Test-Remote-IP";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue(HeaderName, out var value) && IPAddress.TryParse(value.ToString(), out var address))
            {
                context.Connection.RemoteIpAddress = address;
                context.Request.Headers.Remove(HeaderName);
            }

            return nextMiddleware(context);
        });
        next(app);
    };
}

internal static class RequestEdgeRequests
{
    /// <summary>A request from <paramref name="remoteIp"/> (null: no address, like TestServer) with optional extra headers.</summary>
    public static HttpRequestMessage Create(HttpMethod method, string path, string? remoteIp = null, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, path);
        if (remoteIp is not null)
        {
            request.Headers.Add(RemoteIpStartupFilter.HeaderName, remoteIp);
        }

        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return request;
    }
}

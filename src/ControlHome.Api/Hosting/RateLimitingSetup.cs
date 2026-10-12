using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using ControlHome.Api.Http;
using ControlHome.Api.Security;
using ControlHome.Application.Common;
using Microsoft.Extensions.Options;

namespace ControlHome.Api.Hosting;

/// <summary>Rate-limit policy names (budgets per IPv4 address or IPv6 /64).</summary>
public static class RateLimitPolicies
{
    /// <summary>First-run setup: 5 per 10 minutes per IP.</summary>
    public const string Setup = "setup";

    /// <summary>Every password check (login, password change) and token reset, one shared budget: 10 per minute per IP.</summary>
    public const string Login = "login";
}

/// <summary>Fixed-window limits (configuration section <c>RateLimiting</c>; tests may raise them).</summary>
public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public FixedWindowLimit Setup { get; set; } = new() { PermitLimit = 5, Window = TimeSpan.FromMinutes(10) };

    public FixedWindowLimit Login { get; set; } = new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) };

    public sealed class FixedWindowLimit
    {
        public int PermitLimit { get; set; }

        public TimeSpan Window { get; set; }
    }
}

public static class RateLimitingSetup
{
    public static IServiceCollection AddControlHomeRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<RateLimitingOptions>().BindConfiguration(RateLimitingOptions.SectionName);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                var extensions = new Dictionary<string, object?> { [Problems.CodeExtension] = ErrorCodes.RequestRateLimited };
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    var seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
                    context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                    extensions["retryAfterSeconds"] = seconds;
                }

                await TypedResults.Problem(
                        detail: "Too many attempts; try again later.",
                        statusCode: StatusCodes.Status429TooManyRequests,
                        extensions: extensions)
                    .ExecuteAsync(context.HttpContext);
            };

            options.AddPolicy(RateLimitPolicies.Setup, context => PerClientIp(context, limits => limits.Setup));
            options.AddPolicy(RateLimitPolicies.Login, context => PerClientIp(context, limits => limits.Login));
        });

        return services;
    }

    private static RateLimitPartition<string> PerClientIp(
        HttpContext context,
        Func<RateLimitingOptions, RateLimitingOptions.FixedWindowLimit> select)
    {
        var limit = select(context.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value);
        return RateLimitPartition.GetFixedWindowLimiter(
            ClientIp(context.Connection.RemoteIpAddress),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit.PermitLimit,
                Window = limit.Window,
                QueueLimit = 0,
                AutoReplenishment = true,
            });
    }

    /// <summary>The IPv4 address or IPv6 /64 (privacy addresses rotated inside one /64 share a budget).</summary>
    private static string ClientIp(IPAddress? address) => address is null ? "unknown" : ClientNetworkPolicy.SourceKey(address);
}

using System.Globalization;
using ControlHome.Application.Runtime;

namespace ControlHome.Api.Http;

/// <summary>
/// Every API response (errors included) says what time it is on the runtime clock — the clock that state timestamps,
/// history and "last seen" times come from — in <c>X-ControlHome-Time</c> (ISO 8601, UTC, with milliseconds). The UI keeps
/// its own clock's offset from it, so "3 minutes ago" and the end of a chart are right even when a phone's clock is off
/// (or a test moved the runtime clock forward). Added when the response starts.
/// </summary>
internal sealed class ServerTimeHeaderMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-ControlHome-Time";

    private static readonly PathString _api = new("/api");

    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments(_api, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.OnStarting(
                static state =>
                {
                    var http = (HttpContext)state;

                    // Resolved per response: before a database is configured there is no device runtime, only the app clock.
                    var time = http.RequestServices.GetService<IRuntimeClock>()?.Time
                        ?? http.RequestServices.GetService<TimeProvider>()
                        ?? TimeProvider.System;
                    http.Response.Headers[HeaderName] = time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
                    return Task.CompletedTask;
                },
                context);
        }

        return next(context);
    }
}

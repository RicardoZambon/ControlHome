using Microsoft.Net.Http.Headers;

namespace ControlHome.Api.Security;

/// <summary>
/// Adds the Content-Security-Policy, <c>X-Content-Type-Options: nosniff</c>, <c>Referrer-Policy: same-origin</c> and
/// <c>X-Frame-Options: DENY</c> to every response (including errors and responses written after an exception). They are
/// added when the response starts, unless the endpoint set the header itself.
/// </summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next, IHostEnvironment environment)
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; connect-src 'self'; style-src 'self' 'unsafe-inline'; "
        + "script-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'self'";

    /// <summary>
    /// Development only, for Scalar's API reference under <c>/scalar</c>: its page bootstraps with an inline module script,
    /// and it loads its default fonts (Inter, JetBrains Mono) from fonts.scalar.com.
    /// </summary>
    public const string ScalarContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; connect-src 'self'; "
        + "style-src 'self' 'unsafe-inline' https://fonts.scalar.com; font-src 'self' data: https://fonts.scalar.com; "
        + "script-src 'self' 'unsafe-inline' 'unsafe-eval'; worker-src 'self' blob:; frame-ancestors 'none'; object-src 'none'; "
        + "base-uri 'self'";

    private const string ReferrerPolicyHeaderName = "Referrer-Policy";

    private static readonly PathString _scalarPath = new("/scalar");

    private readonly bool _isDevelopment = environment.IsDevelopment();

    public Task InvokeAsync(HttpContext context)
    {
        var policy = _isDevelopment && context.Request.Path.StartsWithSegments(_scalarPath, StringComparison.OrdinalIgnoreCase)
            ? ScalarContentSecurityPolicy
            : ContentSecurityPolicy;

        context.Response.OnStarting(
            static state =>
            {
                var (response, policy) = ((HttpResponse, string))state;
                var headers = response.Headers;
                headers.TryAdd(HeaderNames.ContentSecurityPolicy, policy);
                headers.TryAdd(HeaderNames.XContentTypeOptions, "nosniff");
                headers.TryAdd(ReferrerPolicyHeaderName, "same-origin");
                headers.TryAdd(HeaderNames.XFrameOptions, "DENY");
                return Task.CompletedTask;
            },
            (context.Response, policy));

        return next(context);
    }
}

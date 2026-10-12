using ControlHome.Api.Http;
using ControlHome.Application.Common;
using ControlHome.Infrastructure.Configuration;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace ControlHome.Api.Hosting;

/// <summary>Serves the built Vue SPA from the same origin as the API.</summary>
public static class SpaHostingExtensions
{
    /// <summary>Path prefixes owned by the server; unknown paths below them are 404 ProblemDetails, never index.html.</summary>
    private static readonly string[] _serverPrefixes = ["/api", "/hubs", "/health"];

    /// <summary>
    /// Serves static files from <c>ControlHome:SpaPath</c> (absolute or relative to the content root), else <c>wwwroot</c>.
    /// Call before routing; <see cref="MapControlHomeSpaFallback"/> adds the client-side-route fallback.
    /// </summary>
    public static WebApplication UseControlHomeSpaStaticFiles(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseStaticFiles(CreateStaticFileOptions(app));
        return app;
    }

    /// <summary>
    /// Falls back to <c>index.html</c> for client-side routes and answers unknown <c>/api</c>, <c>/hubs</c> and
    /// <c>/health</c> paths with 404 ProblemDetails (<c>request.not_found</c>), or 405 <c>request.method_not_allowed</c>
    /// with an <c>Allow</c> header when the path exists for other methods. All are anonymous.
    /// </summary>
    public static WebApplication MapControlHomeSpaFallback(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // These catch-alls accept any method, so routing never produces its own 405 below them; they decide themselves.
        var allowedMethods = new AllowedMethodsLookup(app.Services);
        foreach (var prefix in _serverPrefixes)
        {
            app.MapFallback($"{prefix}/{{**path}}", (HttpContext context) => NotFoundOrMethodNotAllowed(context, allowedMethods))
                .AllowAnonymous()
                .ExcludeFromDescription();
        }

        app.MapFallbackToFile("index.html", CreateStaticFileOptions(app))
            .AllowAnonymous()
            .ExcludeFromDescription();

        // A file-like path that is not a static file (e.g. a stale /assets/x.js) is a plain 404. Without this endpoint the
        // request would have no endpoint at all, and the fallback authorization policy would turn it into a 401.
        app.MapFallback(
                "{*path:file}",
                static () => Problems.From(AppError.NotFound(ErrorCodes.RequestNotFound, "No such file.")))
            .AllowAnonymous()
            .ExcludeFromDescription();

        return app;
    }

    private static ProblemHttpResult NotFoundOrMethodNotAllowed(HttpContext context, AllowedMethodsLookup allowedMethods)
    {
        var methods = allowedMethods.For(context);
        if (methods.Count == 0)
        {
            return Problems.From(AppError.NotFound(ErrorCodes.RequestNotFound, "No such endpoint."));
        }

        context.Response.Headers.Allow = string.Join(", ", methods);
        return TypedResults.Problem(
            detail: "This endpoint does not accept the request's method.",
            statusCode: StatusCodes.Status405MethodNotAllowed,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [Problems.CodeExtension] = ErrorCodes.RequestMethodNotAllowed,
            });
    }

    /// <summary>One file provider per app, shared by the static-file middleware and the fallback.</summary>
    private static StaticFileOptions CreateStaticFileOptions(WebApplication app)
    {
        const string PropertyKey = "ControlHome.SpaStaticFileOptions";
        var properties = ((IApplicationBuilder)app).Properties;
        if (properties.TryGetValue(PropertyKey, out var existing) && existing is StaticFileOptions options)
        {
            return options;
        }

        options = BuildStaticFileOptions(app);
        properties[PropertyKey] = options;
        return options;
    }

    private static StaticFileOptions BuildStaticFileOptions(WebApplication app) => new()
    {
        FileProvider = ResolveFileProvider(app),
        OnPrepareResponse = static ctx =>
        {
            // HTML entry points must be revalidated so a new deployment is picked up; hashed assets may be cached.
            if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Context.Response.Headers.CacheControl = "no-cache";
            }
        },
    };

    private static IFileProvider ResolveFileProvider(WebApplication app)
    {
        var configured = app.Services.GetRequiredService<IOptions<ControlHomeOptions>>().Value.SpaPath;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return app.Environment.WebRootFileProvider;
        }

        var fullPath = Path.GetFullPath(configured, app.Environment.ContentRootPath);
        if (!Directory.Exists(fullPath))
        {
            app.Logger.LogWarning("SPA path {SpaPath} does not exist; the web UI will not be served", fullPath);
            return new NullFileProvider();
        }

        return new PhysicalFileProvider(fullPath);
    }
}

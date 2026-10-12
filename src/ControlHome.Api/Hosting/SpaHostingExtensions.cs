using ControlHome.Api.Http;
using ControlHome.Application.Common;
using ControlHome.Infrastructure.Configuration;
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
    /// <c>/health</c> paths with 404 ProblemDetails (<c>request.not_found</c>). Both are anonymous.
    /// </summary>
    public static WebApplication MapControlHomeSpaFallback(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        foreach (var prefix in _serverPrefixes)
        {
            app.MapFallback(
                    $"{prefix}/{{**path}}",
                    static () => Problems.From(AppError.NotFound(ErrorCodes.RequestNotFound, "No such endpoint.")))
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

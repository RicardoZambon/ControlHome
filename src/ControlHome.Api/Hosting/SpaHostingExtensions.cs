using Microsoft.Extensions.FileProviders;

namespace ControlHome.Api.Hosting;

/// <summary>Serves the built Vue SPA from the same origin as the API.</summary>
public static class SpaHostingExtensions
{
    /// <summary>Configuration key for the SPA build output (absolute, or relative to the content root). Defaults to wwwroot.</summary>
    public const string SpaPathKey = "ControlHome:SpaPath";

    /// <summary>Path prefixes owned by the server; unknown paths below them are 404 ProblemDetails, never index.html.</summary>
    private static readonly string[] _serverPrefixes = ["/api", "/hubs", "/health"];

    /// <summary>
    /// Serves static files from <c>ControlHome:SpaPath</c> (or wwwroot), falls back to <c>index.html</c> for client-side routes,
    /// and answers unknown <c>/api</c>, <c>/hubs</c> and <c>/health</c> paths with a 404 ProblemDetails.
    /// </summary>
    public static WebApplication UseControlHomeSpa(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var staticFiles = new StaticFileOptions
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

        app.UseStaticFiles(staticFiles);

        foreach (var prefix in _serverPrefixes)
        {
            app.MapFallback($"{prefix}/{{**path}}", static () => TypedResults.Problem(
                statusCode: StatusCodes.Status404NotFound,
                extensions: new Dictionary<string, object?> { ["code"] = "request.not_found" }));
        }

        app.MapFallbackToFile("index.html", staticFiles);

        return app;
    }

    private static IFileProvider ResolveFileProvider(WebApplication app)
    {
        var configured = app.Configuration[SpaPathKey];
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

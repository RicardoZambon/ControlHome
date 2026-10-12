using System.Text.Json;

namespace ControlHome.Api.IntegrationTests;

/// <summary>The committed, build-generated OpenAPI document (<c>web/packages/api-client/openapi.json</c>).</summary>
internal static class OpenApiDocument
{
    public static string Path { get; } = System.IO.Path.Combine(FindRepositoryRoot(), "web", "packages", "api-client", "openapi.json");

    public static async Task<JsonDocument> LoadAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(Path);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "ControlHome.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"ControlHome.slnx not found above {AppContext.BaseDirectory}.");
    }
}

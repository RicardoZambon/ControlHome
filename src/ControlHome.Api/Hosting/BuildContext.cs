using System.Reflection;

namespace ControlHome.Api.Hosting;

/// <summary>How the process was started.</summary>
internal static class BuildContext
{
    /// <summary>
    /// True when <c>dotnet build</c> runs Program.cs to generate the OpenAPI document (Microsoft.Extensions.ApiDescription.Server
    /// starts the app with a no-op server). Then nothing may touch the database, the network or the data folder.
    /// </summary>
    public static bool IsOpenApiGeneration { get; } =
        string.Equals(Assembly.GetEntryAssembly()?.GetName().Name, "GetDocument.Insider", StringComparison.Ordinal);
}

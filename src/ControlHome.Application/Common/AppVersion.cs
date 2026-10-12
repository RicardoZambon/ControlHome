using System.Reflection;

namespace ControlHome.Application.Common;

/// <summary>The application's version (all ControlHome assemblies are built from the same commit and version).</summary>
public static class AppVersion
{
    /// <summary>The informational version, e.g. <c>1.0.0+&lt;commit&gt;</c>.</summary>
    public static string Informational { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}

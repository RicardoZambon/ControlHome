using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ControlHome.Infrastructure.Configuration;

/// <summary>
/// Resolves <see cref="ControlHomeOptions.DataPath"/> and creates its subfolders lazily (only when something is written),
/// so merely starting the app — or generating the OpenAPI document at build time — never touches the disk.
/// </summary>
public sealed class DataDirectory(IOptions<ControlHomeOptions> options, IHostEnvironment environment)
{
    public const string Keys = "keys";
    public const string Logs = "logs";
    public const string Config = "config";

    /// <summary>The absolute data folder (not created by this call).</summary>
    public string Root => Resolve(options.Value.DataPath, environment.ContentRootPath);

    /// <summary>The absolute path of a subfolder (not created by this call).</summary>
    public string GetPath(string subdirectory) => Path.Combine(Root, subdirectory);

    /// <summary>Creates the subfolder if needed (owner-only on Unix) and returns its absolute path.</summary>
    public string EnsureSubdirectory(string subdirectory)
    {
        var path = GetPath(subdirectory);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    /// <summary>Resolves a configured data path against the content root.</summary>
    public static string Resolve(string dataPath, string contentRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        return Path.GetFullPath(dataPath, contentRootPath);
    }
}

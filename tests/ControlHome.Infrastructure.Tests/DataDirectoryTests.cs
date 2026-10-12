using ControlHome.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Infrastructure.Tests;

public sealed class DataDirectoryTests : IDisposable
{
    private readonly string _parent = Directory.CreateTempSubdirectory("controlhome-data-").FullName;

    [Fact]
    public async Task Resolves_the_data_path_and_creates_subfolders_only_on_demand()
    {
        var dataPath = Path.Combine(_parent, "data");
        await using var services = InfrastructureHost.Build(new Dictionary<string, string?> { ["ControlHome:DataPath"] = dataPath });
        var directory = services.GetRequiredService<DataDirectory>();

        directory.Root.ShouldBe(Path.GetFullPath(dataPath));
        directory.GetPath(DataDirectory.Logs).ShouldBe(Path.Combine(dataPath, "logs"));
        Directory.Exists(dataPath).ShouldBeFalse();

        var keys = directory.EnsureSubdirectory(DataDirectory.Keys);

        Directory.Exists(keys).ShouldBeTrue();
        Directory.Exists(directory.GetPath(DataDirectory.Logs)).ShouldBeFalse();
        Directory.Exists(directory.GetPath(DataDirectory.Config)).ShouldBeFalse();
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(keys).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Relative_paths_resolve_against_the_content_root()
    {
        var contentRoot = Path.Combine(_parent, "src", "ControlHome.Api");

        DataDirectory.Resolve("../../.data", contentRoot).ShouldBe(Path.Combine(_parent, ".data"));
    }

    [Fact]
    public async Task Defaults_to_slash_data()
    {
        await using var services = InfrastructureHost.Build(new Dictionary<string, string?>());

        services.GetRequiredService<DataDirectory>().Root.ShouldBe(Path.GetFullPath("/data"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_parent, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}

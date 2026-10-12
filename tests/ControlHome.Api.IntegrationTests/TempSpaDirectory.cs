namespace ControlHome.Api.IntegrationTests;

/// <summary>A throwaway SPA build output (index.html + one asset) for exercising <c>ControlHome:SpaPath</c>.</summary>
internal sealed class TempSpaDirectory : IDisposable
{
    public const string Marker = "controlhome-spa-test-marker";
    public const string AssetContent = "console.log('controlhome');";

    public TempSpaDirectory()
    {
        Root = Directory.CreateTempSubdirectory("controlhome-spa-").FullName;
        File.WriteAllText(Path.Combine(Root, "index.html"), $"<!doctype html><html><body>{Marker}</body></html>");
        var assets = Directory.CreateDirectory(Path.Combine(Root, "assets"));
        File.WriteAllText(Path.Combine(assets.FullName, "app.js"), AssetContent);
    }

    public string Root { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a file may still be held open by the test server on Windows.
        }
    }
}

using System.Collections.Concurrent;

namespace ControlHome.Connectors.Simulated;

/// <summary>
/// The demo cameras' snapshot images: synthetic scenes bundled with the connector (embedded resources
/// <c>Resources/camera-1.png</c> … <c>camera-4.png</c>; drawn by a script, no real footage).
/// </summary>
internal static class SimulatedSnapshots
{
    public const string ContentType = "image/png";

    private static readonly ConcurrentDictionary<int, byte[]> _images = new();

    /// <summary>The image of camera <paramref name="channel"/> (1-based, 1–4).</summary>
    public static byte[] ForChannel(int channel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channel, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channel, SimulatedDemoDevices.Cameras.Count);
        return _images.GetOrAdd(channel, Load);
    }

    private static byte[] Load(int channel)
    {
        var name = $"ControlHome.Connectors.Simulated.camera-{channel}.png";
        using var stream = typeof(SimulatedSnapshots).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The bundled snapshot '{name}' is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}

namespace ControlHome.Connectors.Abstractions;

/// <summary>
/// Implemented by a runtime whose devices include cameras (endpoints with the <c>camera</c> capability) that can hand out
/// a still image. The host serves it, behind its own authorization, as <c>GET /api/endpoints/{id}/camera/snapshot</c>;
/// connectors never expose camera URLs or credentials to clients.
/// </summary>
public interface ICameraSnapshotSource
{
    /// <summary>
    /// The camera's latest still image, or <c>null</c> when the device or endpoint is unknown or has none right now. Called
    /// only while the runtime runs, concurrently with everything else.
    /// </summary>
    Task<CameraSnapshot?> GetSnapshotAsync(string externalDeviceId, string endpointKey, CancellationToken cancellationToken);
}

/// <summary>A still image of a camera.</summary>
/// <param name="ContentType"><c>image/jpeg</c>, <c>image/png</c> or <c>image/webp</c> (the host refuses anything else).</param>
/// <param name="Data">The encoded image.</param>
/// <param name="TakenAtUtc">When the image was taken, if known.</param>
public sealed record CameraSnapshot(string ContentType, ReadOnlyMemory<byte> Data, DateTimeOffset? TakenAtUtc);

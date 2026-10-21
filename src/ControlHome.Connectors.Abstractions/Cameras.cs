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

/// <summary>
/// Implemented by a runtime whose cameras have a live stream. The host hands the source to its restreamer (the go2rtc
/// sidecar, registered at runtime through its loopback API) and relays only the restreamed media to signed-in clients
/// (<c>/api/endpoints/{id}/camera/stream</c>, <c>/api/endpoints/{id}/camera/webrtc</c>): the source URL and its
/// credentials stay on the server — never in an API response, a log line or a configuration file.
/// </summary>
public interface ICameraStreamSource
{
    /// <summary>
    /// Where the restreamer reads the camera at the endpoint, or <c>null</c> when the device or endpoint is unknown, is not
    /// a camera, or has no stream of that quality. Called only while the runtime runs, concurrently with everything else.
    /// </summary>
    Task<CameraStreamSource?> GetStreamSourceAsync(
        string externalDeviceId,
        string endpointKey,
        CameraStreamQuality quality,
        CancellationToken cancellationToken);
}

/// <summary>Which of a camera's encodings to stream.</summary>
public enum CameraStreamQuality
{
    /// <summary>The main stream: full resolution (Dahua <c>subtype=0</c>).</summary>
    Main,

    /// <summary>The sub (extra) stream: low resolution, light enough for tiles and phones (Dahua <c>subtype=1</c>).</summary>
    Sub,
}

/// <summary>
/// A camera stream for the restreamer: an absolute <c>rtsp://</c> or <c>rtsps://</c> URL, usually with the camera's
/// credentials in its user info. Treat it as a secret: <see cref="ToString"/> never prints it.
/// </summary>
public sealed class CameraStreamSource
{
    /// <exception cref="ArgumentException"><paramref name="url"/> is not an absolute <c>rtsp</c>/<c>rtsps</c> URL.</exception>
    public CameraStreamSource(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != "rtsp" && uri.Scheme != "rtsps")
            || string.IsNullOrEmpty(uri.Host)
            || url.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("A camera stream source must be an absolute rtsp:// or rtsps:// URL.", nameof(url));
        }

        Url = url;
    }

    /// <summary>The URL, credentials included (secret).</summary>
    public string Url { get; }

    /// <summary>Never the URL (it carries credentials).</summary>
    public override string ToString() => "CameraStreamSource(***)";
}

using System.Net.WebSockets;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Common;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Capabilities;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ControlHome.Application.Devices;

/// <summary>
/// The restreamer that turns camera streams (RTSP) into something a browser plays (WebRTC, MSE over a WebSocket): the
/// go2rtc sidecar, reached only by the server on its loopback API. Streams are registered at runtime under opaque names
/// (<see cref="CameraStreamNames"/>), never written to a configuration file; clients reach the media only through the
/// app's own authorized endpoints.
/// </summary>
public interface ICameraRestreamer
{
    /// <summary>Whether a restreamer is configured (<c>Cameras:Go2Rtc:Url</c> and its credentials).</summary>
    bool Enabled { get; }

    /// <summary>Registers (or re-points) the stream <paramref name="streamName"/> at <paramref name="source"/>.</summary>
    /// <exception cref="CameraRestreamerException">The restreamer cannot be reached or refused the stream.</exception>
    Task RegisterAsync(string streamName, CameraStreamSource source, CancellationToken cancellationToken);

    /// <summary>Exchanges a WebRTC SDP offer (receive-only) for the restreamer's answer on a registered stream.</summary>
    /// <exception cref="CameraRestreamerException">The restreamer cannot be reached or refused the offer.</exception>
    Task<string> ExchangeWebRtcAsync(string streamName, string offerSdp, CancellationToken cancellationToken);

    /// <summary>Opens the restreamer's WebSocket for a registered stream (MSE and WebRTC signalling, go2rtc's protocol).</summary>
    /// <exception cref="CameraRestreamerException">The restreamer cannot be reached.</exception>
    Task<WebSocket> ConnectAsync(string streamName, CancellationToken cancellationToken);
}

/// <summary>A restreamer failure; the message never contains a stream source or credentials.</summary>
public sealed class CameraRestreamerException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>No restreamer configured: live view is off (stills still work).</summary>
public sealed class NoCameraRestreamer : ICameraRestreamer
{
    public static NoCameraRestreamer Instance { get; } = new();

    public bool Enabled => false;

    public Task RegisterAsync(string streamName, CameraStreamSource source, CancellationToken cancellationToken) =>
        throw new CameraRestreamerException("No camera restreamer is configured.");

    public Task<string> ExchangeWebRtcAsync(string streamName, string offerSdp, CancellationToken cancellationToken) =>
        throw new CameraRestreamerException("No camera restreamer is configured.");

    public Task<WebSocket> ConnectAsync(string streamName, CancellationToken cancellationToken) =>
        throw new CameraRestreamerException("No camera restreamer is configured.");
}

/// <summary>The restreamer's stream names: opaque ids derived from the endpoint and quality — never a URL.</summary>
public static class CameraStreamNames
{
    public static string For(Guid endpointId, CameraStreamQuality quality) =>
        $"ch_{endpointId:N}_{(quality == CameraStreamQuality.Sub ? "sub" : "main")}";
}

/// <summary>The stream qualities as the API spells them.</summary>
public static class CameraStreamQualities
{
    public const string Main = "main";
    public const string Sub = "sub";

    /// <summary><c>main</c> (also when omitted) or <c>sub</c>; anything else is <c>null</c>.</summary>
    public static CameraStreamQuality? Parse(string? value) => value switch
    {
        null or "" or Main => CameraStreamQuality.Main,
        Sub => CameraStreamQuality.Sub,
        _ => null,
    };
}

/// <summary>A WebRTC offer for a camera's live view (<c>POST /api/endpoints/{id}/camera/webrtc</c>).</summary>
/// <param name="Sdp">The browser's SDP offer; every media section must be receive-only (<c>a=recvonly</c>).</param>
/// <param name="Quality"><c>main</c> (default) or <c>sub</c> (the lighter extra stream, for tiles and phones).</param>
public sealed record CameraWebRtcOfferRequest(string Sdp, string? Quality);

/// <summary>The restreamer's SDP answer (with its ICE candidates).</summary>
public sealed record CameraWebRtcAnswerDto(string Sdp);

/// <summary>A registered live stream, ready to be relayed.</summary>
/// <param name="StreamName">The restreamer's opaque name for it (<see cref="CameraStreamNames"/>).</param>
public sealed record CameraLiveStream(string StreamName);

public sealed class CameraWebRtcOfferRequestValidator : AbstractValidator<CameraWebRtcOfferRequest>
{
    public const int MaxSdpLength = 64 * 1024;

    public CameraWebRtcOfferRequestValidator()
    {
        RuleFor(x => x.Sdp)
            .NotEmpty()
            .MaximumLength(MaxSdpLength)
            .Must(sdp => sdp is not null && sdp.StartsWith("v=0", StringComparison.Ordinal))
            .WithMessage("'{PropertyName}' must be an SDP offer (starting with v=0).")
            .Must(sdp => sdp is null || SdpOffers.IsReceiveOnly(sdp))
            .WithMessage("'{PropertyName}' must only receive media: every audio/video section recvonly (no sending, no data channels).");
        RuleFor(x => x.Quality)
            .Must(quality => CameraStreamQualities.Parse(quality) is not null)
            .WithMessage("'{PropertyName}' must be main or sub.");
    }
}

/// <summary>Checks on browser SDP offers for camera viewing.</summary>
public static class SdpOffers
{
    private static readonly string[] _directions = ["sendrecv", "sendonly", "recvonly", "inactive"];

    /// <summary>
    /// Whether every media section of <paramref name="sdp"/> is audio or video that only receives (<c>recvonly</c> or
    /// <c>inactive</c>, from the media section or else the session level; no direction means <c>sendrecv</c>). Anything
    /// that would send media to the restreamer (publishing into a camera's stream, two-way audio) or open a data channel
    /// is refused.
    /// </summary>
    public static bool IsReceiveOnly(string sdp)
    {
        ArgumentNullException.ThrowIfNull(sdp);
        string? session = null;
        string? media = null;
        string? direction = null;
        var sections = 0;
        foreach (var raw in sdp.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                if (media is not null && !Receives(direction ?? session))
                {
                    return false;
                }

                media = line;
                direction = null;
                sections++;
                if (!(line.StartsWith("m=video ", StringComparison.Ordinal) || line.StartsWith("m=audio ", StringComparison.Ordinal)))
                {
                    return false;
                }
            }
            else if (line.StartsWith("a=", StringComparison.Ordinal) && _directions.Contains(line[2..], StringComparer.Ordinal))
            {
                if (media is null)
                {
                    session = line[2..];
                }
                else
                {
                    direction = line[2..];
                }
            }
        }

        return sections > 0 && Receives(direction ?? session);
    }

    private static bool Receives(string? direction) => direction is "recvonly" or "inactive";
}

/// <summary>
/// Live view behind the app's own authorization: checks that the endpoint is a camera of a running integration, asks its
/// connector for the stream source (credentials included, server-side only) and registers it with the restreamer under an
/// opaque name; the Api then relays only that stream. Errors: 404 <c>endpoints.not_found</c>; 404 <c>cameras.no_stream</c>
/// (not a camera, or its connector has no stream); 503 <c>cameras.live_disabled</c> (no restreamer configured), 503
/// <c>commands.unavailable</c> (its integration is not running), 503 <c>cameras.live_unavailable</c> (the restreamer
/// cannot be reached or refused).
/// </summary>
public sealed partial class CameraStreamService(
    IControlHomeDb db,
    ICommandDispatcher dispatcher,
    ICameraStreamProvider streams,
    ICameraRestreamer restreamer,
    ILogger<CameraStreamService> logger)
{
    /// <summary>Whether live view is set up at all (a restreamer is configured).</summary>
    public bool Enabled => restreamer.Enabled;

    /// <summary>Whether the endpoint exists and is a camera (404 <c>endpoints.not_found</c> / <c>cameras.no_stream</c>); touches no restreamer.</summary>
    public async Task<Result> CheckCameraAsync(Guid endpointId, CancellationToken cancellationToken)
    {
        var camera = await FindAsync(endpointId, cancellationToken);
        return camera.IsFailure ? Result.Failure(camera.Error) : Result.Success();
    }

    /// <summary>Registers the camera's stream with the restreamer and returns its opaque name.</summary>
    public async Task<Result<CameraLiveStream>> OpenAsync(Guid endpointId, CameraStreamQuality quality, CancellationToken cancellationToken)
    {
        var found = await FindAsync(endpointId, cancellationToken);
        if (found.IsFailure)
        {
            return found.Error;
        }

        var camera = found.Value;

        if (!restreamer.Enabled)
        {
            return AppError.Unavailable(ErrorCodes.CamerasLiveDisabled, "Live view is not set up on this server.");
        }

        if (!dispatcher.IsRunning(camera.IntegrationId))
        {
            return AppError.Unavailable(ErrorCodes.CommandsUnavailable, "The camera's integration is not running.");
        }

        var source = await streams.GetStreamSourceAsync(camera.IntegrationId, camera.ExternalId, camera.Key, quality, cancellationToken);
        if (source is null)
        {
            return NoStream();
        }

        var name = CameraStreamNames.For(endpointId, quality);
        try
        {
            await restreamer.RegisterAsync(name, source, cancellationToken);
        }
        catch (CameraRestreamerException ex)
        {
            LogRegisterFailed(name, ex.Message);
            return Unavailable();
        }

        return new CameraLiveStream(name);
    }

    /// <summary>Opens the stream and exchanges the browser's (receive-only) WebRTC offer for the restreamer's answer.</summary>
    public async Task<Result<CameraWebRtcAnswerDto>> ExchangeWebRtcAsync(Guid endpointId, CameraWebRtcOfferRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var opened = await OpenAsync(endpointId, CameraStreamQualities.Parse(request.Quality) ?? CameraStreamQuality.Main, cancellationToken);
        if (opened.IsFailure)
        {
            return opened.Error;
        }

        try
        {
            return new CameraWebRtcAnswerDto(await restreamer.ExchangeWebRtcAsync(opened.Value.StreamName, request.Sdp, cancellationToken));
        }
        catch (CameraRestreamerException ex)
        {
            LogWebRtcFailed(opened.Value.StreamName, ex.Message);
            return Unavailable();
        }
    }

    private async Task<Result<CameraEndpoint>> FindAsync(Guid endpointId, CancellationToken cancellationToken)
    {
        var camera = await (from endpoint in db.Endpoints.AsNoTracking()
                            join device in db.Devices.AsNoTracking() on endpoint.DeviceId equals device.Id
                            where endpoint.Id == endpointId
                            select new
                            {
                                endpoint.Key,
                                device.IntegrationId,
                                device.ExternalId,
                                IsCamera = endpoint.Capabilities.Any(c => c.CapabilityId == CapabilityIds.Camera && !c.Retired),
                            })
            .SingleOrDefaultAsync(cancellationToken);
        if (camera is null)
        {
            return AppError.NotFound(ErrorCodes.EndpointsNotFound, "No such device control.");
        }

        return camera.IsCamera ? new CameraEndpoint(camera.IntegrationId, camera.ExternalId, camera.Key) : NoStream();
    }

    private static AppError NoStream() => AppError.NotFound(ErrorCodes.CamerasNoStream, "The camera has no live stream.");

    private sealed record CameraEndpoint(Guid IntegrationId, string ExternalId, string Key);

    private static AppError Unavailable() => AppError.Unavailable(ErrorCodes.CamerasLiveUnavailable, "The live view service is not available right now.");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Registering camera stream {StreamName} failed: {Reason}")]
    private partial void LogRegisterFailed(string streamName, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WebRTC exchange for camera stream {StreamName} failed: {Reason}")]
    private partial void LogWebRtcFailed(string streamName, string reason);
}

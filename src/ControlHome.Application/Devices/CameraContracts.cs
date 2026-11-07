using ControlHome.Connectors.Abstractions;
using FluentValidation;

namespace ControlHome.Application.Devices;

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

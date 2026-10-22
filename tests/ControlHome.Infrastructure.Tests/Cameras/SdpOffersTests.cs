using ControlHome.Application.Devices;
using ControlHome.Connectors.Abstractions;

namespace ControlHome.Infrastructure.Tests.Cameras;

/// <summary>Only receive-only WebRTC offers reach go2rtc (no publishing into a camera's stream, no two-way audio).</summary>
public sealed class SdpOffersTests
{
    private const string Session = "v=0\r\no=- 4611731400430051336 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\na=group:BUNDLE 0 1\r\n";

    [Fact]
    public void A_browser_viewer_offer_is_receive_only()
    {
        var sdp = Session
            + "m=video 9 UDP/TLS/RTP/SAVPF 96 97\r\nc=IN IP4 0.0.0.0\r\na=mid:0\r\na=recvonly\r\na=rtpmap:96 H264/90000\r\n"
            + "m=audio 9 UDP/TLS/RTP/SAVPF 111\r\nc=IN IP4 0.0.0.0\r\na=mid:1\r\na=recvonly\r\na=rtpmap:111 opus/48000/2\r\n";

        SdpOffers.IsReceiveOnly(sdp).ShouldBeTrue();
        SdpOffers.IsReceiveOnly(sdp.Replace("\r\n", "\n", StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("m=video 9 UDP/TLS/RTP/SAVPF 96\r\na=sendrecv\r\n")]
    [InlineData("m=video 9 UDP/TLS/RTP/SAVPF 96\r\na=sendonly\r\n")]
    [InlineData("m=video 9 UDP/TLS/RTP/SAVPF 96\r\n")]
    [InlineData("m=video 9 UDP/TLS/RTP/SAVPF 96\r\na=recvonly\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\na=sendonly\r\n")]
    [InlineData("m=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\na=recvonly\r\n")]
    [InlineData("")]

    // Two directions in one section: go2rtc's WebRTC stack (pion) takes the first one, so the check must not take the last.
    [InlineData("m=audio 9 UDP/TLS/RTP/SAVPF 111\r\na=sendrecv\r\na=recvonly\r\n")]
    [InlineData("m=audio 9 UDP/TLS/RTP/SAVPF 111\r\na=recvonly\r\na=sendrecv\r\n")]
    public void Offers_that_would_send_media_or_open_channels_are_refused(string media) =>
        SdpOffers.IsReceiveOnly(Session + media).ShouldBeFalse();

    [Fact]
    public void An_ambiguous_session_level_direction_is_refused() =>
        SdpOffers.IsReceiveOnly("v=0\r\ns=-\r\na=sendrecv\r\na=recvonly\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\n").ShouldBeFalse();

    [Fact]
    public void A_session_level_direction_applies_to_sections_without_their_own()
    {
        var sdp = "v=0\r\ns=-\r\na=recvonly\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\na=sendrecv\r\n";

        SdpOffers.IsReceiveOnly(sdp).ShouldBeFalse("the audio section overrides it");
        SdpOffers.IsReceiveOnly("v=0\r\ns=-\r\na=recvonly\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\n").ShouldBeTrue();
    }

    [Fact]
    public void The_offer_request_is_validated()
    {
        var validator = new CameraWebRtcOfferRequestValidator();
        var ok = Session + "m=video 9 UDP/TLS/RTP/SAVPF 96\r\na=recvonly\r\n";

        validator.Validate(new CameraWebRtcOfferRequest(ok, null)).IsValid.ShouldBeTrue();
        validator.Validate(new CameraWebRtcOfferRequest(ok, "sub")).IsValid.ShouldBeTrue();
        validator.Validate(new CameraWebRtcOfferRequest(ok, "4k")).Errors.ShouldHaveSingleItem().PropertyName.ShouldBe("Quality");
        validator.Validate(new CameraWebRtcOfferRequest("hello", null)).Errors.ShouldContain(e => e.PropertyName == "Sdp");
        validator.Validate(new CameraWebRtcOfferRequest(Session + "m=video 9 RTP 96\r\na=sendrecv\r\n", null)).IsValid.ShouldBeFalse();
        validator.Validate(new CameraWebRtcOfferRequest(ok + new string('a', CameraWebRtcOfferRequestValidator.MaxSdpLength), null)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Stream_names_are_opaque_per_endpoint_and_quality()
    {
        var endpoint = Guid.Parse("8c5b1c7e-2f1a-4d7b-9a51-0d6a2c1f3e44");

        CameraStreamNames.For(endpoint, CameraStreamQuality.Main).ShouldBe("ch_8c5b1c7e2f1a4d7b9a510d6a2c1f3e44_main");
        CameraStreamNames.For(endpoint, CameraStreamQuality.Sub).ShouldBe("ch_8c5b1c7e2f1a4d7b9a510d6a2c1f3e44_sub");
        CameraStreamQualities.Parse(null).ShouldBe(CameraStreamQuality.Main);
        CameraStreamQualities.Parse("sub").ShouldBe(CameraStreamQuality.Sub);
        CameraStreamQualities.Parse("Main").ShouldBeNull();
    }
}

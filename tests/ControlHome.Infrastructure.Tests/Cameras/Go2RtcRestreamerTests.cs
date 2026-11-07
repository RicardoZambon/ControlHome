using System.Text;
using System.Text.Json;
using ControlHome.Application.Devices;
using ControlHome.Application.Security;
using ControlHome.Connectors.Abstractions;
using ControlHome.Infrastructure.Cameras;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ControlHome.Infrastructure.Tests.Cameras;

/// <summary>The go2rtc API client against WireMock: only PATCH streams and POST webrtc, Basic auth, nothing persisted, no leaks.</summary>
public sealed class Go2RtcRestreamerTests : IDisposable
{
    private const string User = "go2rtc-test-user";
    private const string ApiPassword = "go2rtc-test-secret-5f2a";

    // A synthetic camera URL; the password has characters that must be escaped twice (once in the URL, once in the query).
    private static readonly string _camera = string.Concat("rtsp://viewer:", Uri.EscapeDataString("cam/pa:ss@1"), "@192.0.2.20:554/cam/realmonitor?channel=1&subtype=1");

    private readonly WireMockServer _go2rtc = WireMockServer.Start();
    private readonly KnownSecrets _secrets = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _go2rtc.Stop();

    [Theory]
    [InlineData(null, User, ApiPassword)]
    [InlineData("http://127.0.0.1:1984", null, ApiPassword)]
    [InlineData("http://127.0.0.1:1984", User, "")]
    [InlineData("http://user:pass@127.0.0.1:1984", User, ApiPassword)]
    [InlineData("ftp://127.0.0.1:1984", User, ApiPassword)]
    [InlineData("http://127.0.0.1:1984/?x=1", User, ApiPassword)]
    public async Task Live_view_is_off_unless_url_and_credentials_are_set(string? url, string? user, string? apiPassword)
    {
        var restreamer = Create(url, user, apiPassword);

        restreamer.Enabled.ShouldBeFalse();
        await Should.ThrowAsync<CameraRestreamerException>(() => restreamer.RegisterAsync("ch_1_main", new CameraStreamSource(_camera), Ct));
    }

    [Fact]
    public async Task A_stream_is_registered_in_memory_with_patch_and_basic_auth()
    {
        _go2rtc.Given(Request.Create().UsingPatch().WithPath("/api/streams")).RespondWith(Response.Create().WithStatusCode(200));
        var restreamer = Create(_go2rtc.Url);

        await restreamer.RegisterAsync("ch_0123abcd_main", new CameraStreamSource(_camera), Ct);

        var request = _go2rtc.LogEntries.ShouldHaveSingleItem().RequestMessage!;
        request.Method.ShouldBe("PATCH", "PUT would also write the stream into go2rtc.yaml");
        request.Path.ShouldBe("/api/streams");
        request.Query!["name"].ShouldHaveSingleItem().ShouldBe("ch_0123abcd_main");
        // go2rtc's RTSP client asks cameras for a back channel (two-way audio) unless told not to: never, here.
        request.Query!["src"].ShouldHaveSingleItem().ShouldBe(_camera + "#backchannel=0");
        request.Headers!["Authorization"].ShouldHaveSingleItem().ShouldBe("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{ApiPassword}")));

        // From now on every log line masks the camera URL and its password, escaped or not.
        _secrets.Redact($"src={_camera}").ShouldNotContain("192.0.2.20");
        _secrets.Redact("password cam/pa:ss@1").ShouldNotContain("cam/pa:ss@1");
        _secrets.Redact(ApiPassword).ShouldBe(KnownSecrets.Mask);
    }

    [Fact]
    public async Task A_refusal_never_quotes_the_source_or_the_answer()
    {
        _go2rtc.Given(Request.Create().UsingPatch().WithPath("/api/streams"))
            .RespondWith(Response.Create().WithStatusCode(400).WithBody($"streams: bad source {_camera}"));
        var restreamer = Create(_go2rtc.Url);

        var failure = await Should.ThrowAsync<CameraRestreamerException>(() => restreamer.RegisterAsync("ch_1_main", new CameraStreamSource(_camera), Ct));

        failure.Message.ShouldBe("go2rtc refused the stream (HTTP 400).");
        failure.ToString().ShouldNotContain("192.0.2.20");
    }

    [Theory]
    [InlineData("rtsp://192.0.2.20/x")]
    [InlineData("../api/config")]
    [InlineData("a b")]
    [InlineData("")]
    public async Task Stream_names_are_opaque_ids_never_urls_or_paths(string name)
    {
        var restreamer = Create(_go2rtc.Url);

        await Should.ThrowAsync<ArgumentException>(() => restreamer.RegisterAsync(name, new CameraStreamSource(_camera), Ct));
        await Should.ThrowAsync<ArgumentException>(() => restreamer.ExchangeWebRtcAsync(name, "v=0", Ct));
        _go2rtc.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public void Only_rtsp_sources_exist()
    {
        Should.Throw<ArgumentException>(() => new CameraStreamSource("exec:ffmpeg -i /dev/video0"));
        Should.Throw<ArgumentException>(() => new CameraStreamSource("ffmpeg:rtsp://192.0.2.20/x#video=h264"));
        Should.Throw<ArgumentException>(() => new CameraStreamSource("http://192.0.2.20/snapshot.jpg"));
        Should.Throw<ArgumentException>(() => new CameraStreamSource("rtsp://192.0.2.20/x y"));
        new CameraStreamSource(_camera).ToString().ShouldNotContain("viewer");
    }

    [Fact]
    public async Task A_webrtc_offer_is_exchanged_as_json()
    {
        _go2rtc.Given(Request.Create().UsingPost().WithPath("/api/webrtc").WithParam("src", "ch_1_sub"))
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody("""{"type":"answer","sdp":"v=0\r\no=- 1 1 IN IP4 192.0.2.5\r\n"}"""));
        var restreamer = Create(_go2rtc.Url);

        var answer = await restreamer.ExchangeWebRtcAsync("ch_1_sub", "v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=recvonly\r\n", Ct);

        answer.ShouldStartWith("v=0\r\no=-");
        var request = _go2rtc.LogEntries.ShouldHaveSingleItem().RequestMessage!;
        request.Headers!["Content-Type"].ShouldHaveSingleItem().ShouldStartWith("application/json");
        using var body = JsonDocument.Parse(request.Body!);
        body.RootElement.GetProperty("type").GetString().ShouldBe("offer");
        body.RootElement.GetProperty("sdp").GetString().ShouldStartWith("v=0");
        request.Headers!.ContainsKey("Authorization").ShouldBeTrue();
    }

    [Theory]
    [InlineData(500, """{"type":"answer","sdp":"v=0"}""")]
    [InlineData(200, "not json")]
    [InlineData(200, """{"type":"offer","sdp":"v=0"}""")]
    [InlineData(200, """{"type":"answer","sdp":""}""")]
    public async Task A_failed_or_odd_webrtc_answer_is_a_restreamer_failure(int status, string body)
    {
        _go2rtc.Given(Request.Create().UsingPost().WithPath("/api/webrtc")).RespondWith(Response.Create().WithStatusCode(status).WithBody(body));
        var restreamer = Create(_go2rtc.Url);

        await Should.ThrowAsync<CameraRestreamerException>(() => restreamer.ExchangeWebRtcAsync("ch_1_main", "v=0\r\n", Ct));
    }

    [Fact]
    public async Task An_unreachable_go2rtc_is_a_restreamer_failure()
    {
        var url = _go2rtc.Url!;
        _go2rtc.Stop();
        var restreamer = Create(url);

        await Should.ThrowAsync<CameraRestreamerException>(() => restreamer.RegisterAsync("ch_1_main", new CameraStreamSource(_camera), Ct));
        await Should.ThrowAsync<CameraRestreamerException>(() => restreamer.ConnectAsync("ch_1_main", Ct));
    }

    private Go2RtcRestreamer Create(string? url, string? user = User, string? apiPassword = ApiPassword) => new(
        Options.Create(new Go2RtcOptions { Url = url, Username = user, Password = apiPassword, RequestTimeout = TimeSpan.FromSeconds(5) }),
        () => new HttpClient(),
        _secrets,
        TimeProvider.System,
        NullLogger<Go2RtcRestreamer>.Instance);
}

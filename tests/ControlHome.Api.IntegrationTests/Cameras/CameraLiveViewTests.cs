using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using ControlHome.Api.Features.Cameras;
using ControlHome.Api.IntegrationTests.Devices;
using ControlHome.Application.Auth;
using ControlHome.Application.Devices;
using ControlHome.Connectors.Abstractions;
using ControlHome.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ControlHome.Api.IntegrationTests.Cameras;

/// <summary>The API with a fake go2rtc sidecar configured and the Simulated demo cameras streaming a (synthetic) RTSP source.</summary>
public sealed class CamerasApiFactory(SqlServerFixture sql) : DevicesApiFactory(sql)
{
    /// <summary>
    /// The synthetic camera password in the demo cameras' stream URLs (must never reach a client). Built at run time so
    /// the secret scanner does not read the test's made-up credentials as real ones.
    /// </summary>
    public static readonly string StreamPassword = string.Concat("synthetic", "-cam-", "pass-41");

    public static readonly string StreamTemplate = StreamUrl("demo{n}");

    /// <summary>A demo camera's stream URL with the synthetic user and password.</summary>
    public static string StreamUrl(string path) => string.Concat("rtsp", "://viewer:", StreamPassword, "@198.51.100.10:8554/", path);

    public FakeGo2Rtc Go2Rtc { get; private set; } = null!;

    public override async ValueTask InitializeAsync()
    {
        Go2Rtc = await FakeGo2Rtc.StartAsync();
        await base.InitializeAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await Go2Rtc.DisposeAsync();
    }

    protected override void ConfigureSettings(IDictionary<string, string?> settings)
    {
        base.ConfigureSettings(settings);
        settings["Cameras:Go2Rtc:Url"] = Go2Rtc.Url;
        settings["Cameras:Go2Rtc:Username"] = FakeGo2Rtc.User;
        settings["Cameras:Go2Rtc:Password"] = FakeGo2Rtc.Password;
        settings["Simulated:CameraStreamSource"] = StreamTemplate;
    }
}

[CollectionDefinition(Name)]
public sealed class CamerasCollection : ICollectionFixture<CamerasApiFactory>
{
    public const string Name = "Cameras";
}

/// <summary>
/// Live view through the app: a Viewer watches (WebSocket relay, WebRTC exchange), unknown and non-camera endpoints are
/// 404, and go2rtc never sees anything but its three viewer paths with the server's opaque stream names.
/// </summary>
[Collection(CamerasCollection.Name)]
public sealed class CameraLiveViewTests(CamerasApiFactory factory) : DevicesTestBase(factory)
{
    private const string SameOrigin = "http://localhost";
    private const string ReceiveOnlyOffer = "v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=recvonly\r\na=rtpmap:96 H264/90000\r\n";

    private FakeGo2Rtc Go2Rtc => ((CamerasApiFactory)Factory).Go2Rtc;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        Go2Rtc.Clear();
    }

    [Fact]
    public async Task A_viewer_watches_through_the_relay_and_go2rtc_gets_only_opaque_stream_names()
    {
        var (admin, _, viewer) = await SignInAllAsync();
        await CreateIntegrationAsync(admin, config: """{"preset":"demo"}""");
        var camera = MainEndpoint((await WaitForDevicesAsync(viewer, 20)).Single(d => d.Name == "Gate camera")).Id;
        var cookie = await SessionCookieAsync("viewer");

        // The client's own src/dst are ignored: the server picks the stream.
        using (var socket = await ConnectAsync($"/api/endpoints/{camera}/camera/stream?quality=sub&src=exec:evil&dst=x", cookie))
        {
            await SendTextAsync(socket, """{"type":"exec","value":"rm -rf /"}""");
            await SendTextAsync(socket, $$"""{"type":"webrtc/offer","value":"{{ReceiveOnlyOffer.Replace("recvonly", "sendrecv", StringComparison.Ordinal).Replace("\r\n", "\\r\\n", StringComparison.Ordinal)}}"}""");
            await SendTextAsync(socket, """{"type":"mse","value":"avc1.640029,mp4a.40.2"}""");

            var (type, text) = await ReceiveAsync(socket);
            (type, text).ShouldBe((WebSocketMessageType.Text, """{"type":"mse","value":"video/mp4; codecs=\"avc1.640029\""}"""));
            (await ReceiveAsync(socket)).Type.ShouldBe(WebSocketMessageType.Binary);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Ct);
        }

        Go2Rtc.SocketMessages.ToList().ShouldBe(["""{"type":"mse","value":"avc1.640029,mp4a.40.2"}"""], "only viewer-protocol messages are relayed");

        using (var webrtc = await viewer.PostAsJsonAsync($"/api/endpoints/{camera}/camera/webrtc", new CameraWebRtcOfferRequest(ReceiveOnlyOffer, null), Ct))
        {
            await webrtc.ShouldHaveStatusAsync(HttpStatusCode.OK);
            (await webrtc.Content.ReadFromJsonAsync<CameraWebRtcAnswerDto>(Ct))!.Sdp.ShouldBe(FakeGo2Rtc.Answer);
        }

        var sub = CameraStreamNames.For(camera, CameraStreamQuality.Sub);
        var main = CameraStreamNames.For(camera, CameraStreamQuality.Main);
        var requests = Go2Rtc.Requests.ToList();
        requests.Select(r => (r.Method, r.Path)).ShouldBe([("PATCH", "/api/streams"), ("GET", "/api/ws"), ("PATCH", "/api/streams"), ("POST", "/api/webrtc")]);
        requests.ShouldAllBe(r => r.Authorization == FakeGo2Rtc.ExpectedAuthorization);
        (requests[0].Param("name"), requests[0].Param("src")).ShouldBe((sub, CamerasApiFactory.StreamUrl("demo1#backchannel=0")));
        (requests[1].Param("src"), requests[1].WebSocket).ShouldBe((sub, true));
        requests[1].Param("dst").ShouldBeNull();
        (requests[2].Param("name"), requests[3].Param("src")).ShouldBe((main, main));
        requests.ShouldNotContain(r => r.Query.Contains("evil", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unknown_and_non_camera_endpoints_are_404_and_nothing_reaches_go2rtc()
    {
        var (admin, _, viewer) = await SignInAllAsync();
        await CreateIntegrationAsync(admin, config: """{"preset":"demo"}""");
        var devices = await WaitForDevicesAsync(viewer, 20);
        var camera = MainEndpoint(devices.Single(d => d.Name == "Gate camera")).Id;
        var light = MainEndpoint(devices.Single(d => d.Name == "Living room light")).Id;

        foreach (var (id, code) in new[] { (Guid.NewGuid(), "endpoints.not_found"), (light, "cameras.no_stream") })
        {
            using var stream = await viewer.GetAsync($"/api/endpoints/{id}/camera/stream", Ct);
            await stream.ShouldBeProblemAsync(HttpStatusCode.NotFound, code);
            using var webrtc = await viewer.PostAsJsonAsync($"/api/endpoints/{id}/camera/webrtc", new CameraWebRtcOfferRequest(ReceiveOnlyOffer, null), Ct);
            await webrtc.ShouldBeProblemAsync(HttpStatusCode.NotFound, code);
        }

        var cookie = await SessionCookieAsync("viewer");
        var refused = await Should.ThrowAsync<InvalidOperationException>(() => ConnectAsync($"/api/endpoints/{Guid.NewGuid()}/camera/stream", cookie));
        refused.Message.ShouldContain("404");

        using (var plain = await viewer.GetAsync($"/api/endpoints/{camera}/camera/stream", Ct))
        {
            await plain.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "request.websocket_required");
        }

        using (var quality = await viewer.GetAsync($"/api/endpoints/{camera}/camera/stream?quality=4k", Ct))
        {
            await quality.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation.failed");
        }

        using (var sending = await viewer.PostAsJsonAsync($"/api/endpoints/{camera}/camera/webrtc", new CameraWebRtcOfferRequest(ReceiveOnlyOffer.Replace("recvonly", "sendonly", StringComparison.Ordinal), null), Ct))
        {
            await sending.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation.failed");
            (await sending.BodyAsync()).GetProperty("errors").TryGetProperty("sdp", out _).ShouldBeTrue();
        }

        using var anonymous = Factory.CreateApiClient();
        (await anonymous.GetAsync($"/api/endpoints/{camera}/camera/stream", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync($"/api/endpoints/{camera}/camera/webrtc", new CameraWebRtcOfferRequest(ReceiveOnlyOffer, null), Ct)).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        await Should.ThrowAsync<InvalidOperationException>(() => ConnectAsync($"/api/endpoints/{camera}/camera/stream", cookie: null));

        // A foreign page cannot open a viewer's stream (the request edge checks the Origin of every upgrade).
        var crossOrigin = await Should.ThrowAsync<InvalidOperationException>(() => ConnectAsync($"/api/endpoints/{camera}/camera/stream", cookie, "http://evil.example"));
        crossOrigin.Message.ShouldContain("403");

        Go2Rtc.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task No_other_go2rtc_path_is_reachable_through_the_backend()
    {
        var (admin, _, _) = await SignInAllAsync();
        await CreateIntegrationAsync(admin, config: """{"preset":"demo"}""");
        var camera = MainEndpoint((await WaitForDevicesAsync(admin, 20)).Single(d => d.Name == "Gate camera")).Id;
        string[] paths =
        [
            "/api/streams", "/api/streams?src=x", "/api/ws?src=x", "/api/webrtc?src=x", "/api/config", "/api/frame.jpeg?src=x",
            "/api/frame.mp4?src=x", "/api/stream.mp4?src=x", "/api/exit", "/api/restart", "/api/log", "/api",
            $"/api/endpoints/{camera}/camera/streams", $"/api/endpoints/{camera}/camera/api/streams", $"/api/endpoints/{camera}/camera/config",
            $"/api/endpoints/{camera}/camera/stream/../../../streams", $"/api/endpoints/{camera}/camera/stream%2F..%2F..%2Fconfig",
            $"/api/endpoints/{camera}/camera/ws", $"/api/endpoints/{camera}/camera/frame.jpeg",
        ];

        foreach (var path in paths)
        {
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
            {
                using var request = new HttpRequestMessage(method, path);
                using var response = await admin.SendAsync(request, Ct);
                ((int)response.StatusCode).ShouldBeGreaterThanOrEqualTo(400, $"{method} {path}");
            }
        }

        using (var wrongMethod = await admin.PostAsync($"/api/endpoints/{camera}/camera/stream", null, Ct))
        {
            ((int)wrongMethod.StatusCode).ShouldBe(405);
        }

        using (var wrongMethod = await admin.GetAsync($"/api/endpoints/{camera}/camera/webrtc", Ct))
        {
            ((int)wrongMethod.StatusCode).ShouldBe(405);
        }

        Go2Rtc.Requests.ShouldBeEmpty("no backend path reaches go2rtc except the camera stream and WebRTC endpoints");
    }

    [Fact]
    public void Only_viewer_protocol_messages_are_relayed_to_go2rtc()
    {
        Relayed("""{"type":"mse","value":"avc1.640029"}""").ShouldBe("""{"type":"mse","value":"avc1.640029"}""");
        Relayed("""{"type":"mp4","value":""}""").ShouldBe("""{"type":"mp4","value":""}""");
        Relayed("""{"type":"mjpeg"}""").ShouldBe("""{"type":"mjpeg"}""");
        Relayed("""{"type":"webrtc/candidate","value":"candidate:1 1 udp 1 192.0.2.7 50000 typ host"}""")
            .ShouldBe("""{"type":"webrtc/candidate","value":"candidate:1 1 udp 1 192.0.2.7 50000 typ host"}""");
        Relayed($$"""{"type":"webrtc/offer","value":"{{Escape(ReceiveOnlyOffer)}}"}""").ShouldBe($$"""{"type":"webrtc/offer","value":"{{Escape(ReceiveOnlyOffer)}}"}""");
        Relayed($$"""{"type":"webrtc/offer","value":"{{Escape(ReceiveOnlyOffer.Replace("recvonly", "sendrecv", StringComparison.Ordinal))}}"}""").ShouldBeNull();
        Relayed("""{"type":"webrtc","value":{"type":"offer","sdp":"v=0"}}""").ShouldBeNull();
        Relayed("""{"type":"hls","value":""}""").ShouldBeNull();
        Relayed("""{"type":"exec"}""").ShouldBeNull();
        Relayed("""{"type":"mse","value":{"codecs":"avc1"}}""").ShouldBeNull("viewer values are strings");
        Relayed("""["mse"]""").ShouldBeNull();
        Relayed("""{"type":"mse"} {"type":"webrtc"}""").ShouldBeNull();
        Relayed("not json").ShouldBeNull();
    }

    [Fact]
    public void Messages_go2rtc_would_read_differently_are_refused_and_the_rest_is_rewritten()
    {
        // go2rtc decodes with Go's encoding/json: object keys match case-insensitively and the last duplicate wins. A
        // message checked as "mse" (or as a receive-only offer) must never reach it as a sending WebRTC offer.
        var sending = Escape(ReceiveOnlyOffer.Replace("recvonly", "sendrecv", StringComparison.Ordinal));
        Relayed($$"""{"type":"mse","TYPE":"webrtc/offer","value":"{{sending}}"}""").ShouldBeNull();
        Relayed($$"""{"type":"mse","type":"webrtc/offer","value":"{{sending}}"}""").ShouldBeNull();
        Relayed($$"""{"type":"webrtc/offer","value":"{{Escape(ReceiveOnlyOffer)}}","Value":"{{sending}}"}""").ShouldBeNull();
        Relayed($$"""{"type":"webrtc/offer","value":"{{Escape(ReceiveOnlyOffer)}}","value":"{{sending}}"}""").ShouldBeNull();

        // Anything else go2rtc gets is written anew: only type and value, nothing the viewer added.
        Relayed("""{"value":"avc1.640029","src":"exec:x","type":"mse","dst":{"x":1}}""").ShouldBe("""{"type":"mse","value":"avc1.640029"}""");
    }

    [Fact]
    public async Task A_view_starts_at_most_a_few_go2rtc_consumers_and_never_shows_a_camera_password()
    {
        var (admin, _, viewer) = await SignInAllAsync();
        await CreateIntegrationAsync(admin, config: """{"preset":"demo"}""");
        var camera = MainEndpoint((await WaitForDevicesAsync(viewer, 20)).Single(d => d.Name == "Gate camera")).Id;
        var cookie = await SessionCookieAsync("viewer");
        using var socket = await ConnectAsync($"/api/endpoints/{camera}/camera/stream", cookie);

        // Each mse/mp4/mjpeg/webrtc offer adds a consumer in go2rtc (a copy of the camera's stream): a few per view only.
        for (var i = 0; i < CameraStreamRelay.MaxStreamStartsPerView + 2; i++)
        {
            await SendTextAsync(socket, """{"type":"mse","value":"avc1.640029"}""");
        }

        const string Candidate = """{"type":"webrtc/candidate","value":"candidate:1 1 udp 1 192.0.2.7 50000 typ host"}""";
        await SendTextAsync(socket, Candidate);
        for (var i = 0; i < 2 * (CameraStreamRelay.MaxStreamStartsPerView + 1); i++)
        {
            await ReceiveAsync(socket);
        }

        Go2Rtc.SocketMessages.ToList().ShouldBe(
            [.. Enumerable.Repeat("""{"type":"mse","value":"avc1.640029"}""", CameraStreamRelay.MaxStreamStartsPerView), Candidate]);

        // go2rtc's answers pass through the log redactor: an error quoting the camera's source keeps no password.
        await SendTextAsync(socket, $$"""{"type":"webrtc/candidate","value":"{{FakeGo2Rtc.QuoteSourceValue}}"}""");
        var (type, error) = await ReceiveAsync(socket);
        type.ShouldBe(WebSocketMessageType.Text);
        error.ShouldContain("198.51.100.10");
        error.ShouldNotContain(CamerasApiFactory.StreamPassword);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Ct);
    }

    [Fact]
    public async Task The_stream_also_answers_http2_websocket_connects()
    {
        // Browsers open WebSockets over HTTP/2 (HTTPS) with an extended CONNECT, which a GET-only route would refuse (405).
        var (admin, _, viewer) = await SignInAllAsync();
        await CreateIntegrationAsync(admin, config: """{"preset":"demo"}""");
        var camera = MainEndpoint((await WaitForDevicesAsync(viewer, 20)).Single(d => d.Name == "Gate camera")).Id;

        using var request = new HttpRequestMessage(HttpMethod.Connect, $"/api/endpoints/{camera}/camera/stream");
        using var response = await viewer.SendAsync(request, Ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "request.websocket_required");
        Go2Rtc.Requests.ShouldBeEmpty();
    }

    private static string? Relayed(string json) =>
        CameraStreamRelay.Canonicalize(Encoding.UTF8.GetBytes(json)) is { } message ? Encoding.UTF8.GetString(message.Json) : null;

    private static string Escape(string sdp) => sdp.Replace("\r\n", "\\r\\n", StringComparison.Ordinal);

    private Task<WebSocket> ConnectAsync(string path, string? cookie, string origin = SameOrigin)
    {
        var client = Factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers.Origin = origin;
            if (cookie is not null)
            {
                request.Headers.Cookie = cookie;
            }
        };
        return client.ConnectAsync(new Uri(new Uri("ws://localhost"), path), Ct);
    }

    private static Task SendTextAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, Ct);

    private static async Task<(WebSocketMessageType Type, string Text)> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        return (result.MessageType, Encoding.UTF8.GetString(buffer, 0, result.Count));
    }

    /// <summary>Signs in and returns the session cookie (<c>name=value</c>).</summary>
    private async Task<string> SessionCookieAsync(string userName)
    {
        using var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest(userName, Password, RememberMe: false)),
        };
        request.Headers.Add("Origin", SameOrigin);
        request.Headers.Add("X-ControlHome", "1");
        using var response = await client.SendAsync(request, Ct);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        var setCookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("ControlHome.Session=", StringComparison.Ordinal));
        return setCookie.Split(';', 2)[0];
    }
}

/// <summary>Without a go2rtc restreamer configured, live view is off (stills keep working).</summary>
[Collection(DevicesCollection.Name)]
public sealed class CameraLiveDisabledTests(DevicesApiFactory factory) : DevicesTestBase(factory)
{
    [Fact]
    public async Task Live_view_is_503_live_disabled_for_cameras_and_still_404_for_others()
    {
        var (admin, _, viewer) = await SignInAllAsync();
        await CreateIntegrationAsync(admin, config: """{"preset":"demo"}""");
        var devices = await WaitForDevicesAsync(viewer, 20);
        var camera = MainEndpoint(devices.Single(d => d.Name == "Gate camera")).Id;
        var offer = new CameraWebRtcOfferRequest("v=0\r\ns=-\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=recvonly\r\n", "main");

        using (var webrtc = await viewer.PostAsJsonAsync(string.Create(CultureInfo.InvariantCulture, $"/api/endpoints/{camera}/camera/webrtc"), offer, Ct))
        {
            await webrtc.ShouldBeProblemAsync(HttpStatusCode.ServiceUnavailable, "cameras.live_disabled");
        }

        using (var unknown = await viewer.PostAsJsonAsync($"/api/endpoints/{Guid.NewGuid()}/camera/webrtc", offer, Ct))
        {
            await unknown.ShouldBeProblemAsync(HttpStatusCode.NotFound, "endpoints.not_found");
        }

        using var snapshot = await viewer.GetAsync($"/api/endpoints/{camera}/camera/snapshot", Ct);
        await snapshot.ShouldHaveStatusAsync(HttpStatusCode.OK);
    }
}

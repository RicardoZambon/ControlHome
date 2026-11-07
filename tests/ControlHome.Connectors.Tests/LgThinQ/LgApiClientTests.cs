using System.Text.Json.Nodes;
using ControlHome.Connectors.LgThinQ;
using ControlHome.Connectors.LgThinQ.Api;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace ControlHome.Connectors.Tests.LgThinQ;

/// <summary>The REST client against WireMock: the SDK's headers, payload unwrapping, error codes, quota and GET-only retries.</summary>
public sealed class LgApiClientTests : IDisposable
{
    private const string Pat = "test-pat-not-a-real-token";
    private const string ClientId = "controlhome-00000000-0000-4000-8000-000000000001";

    private readonly FakeLgCloud _cloud = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _cloud.Dispose();

    [Fact]
    public async Task Every_request_carries_the_sdks_headers_and_answers_are_unwrapped()
    {
        _cloud.Ok("GET", "/devices", LgFixtures.Text("devices.json"));
        _cloud.Ok("POST", "/devices/test-ac-0001/control", "{}");
        using var factory = Factory();
        var api = Api(factory);

        var devices = await api.GetDevicesAsync(Ct);
        await api.ControlAsync("test-ac-0001", new JsonObject { ["operation"] = new JsonObject { ["airConOperationMode"] = "POWER_ON" } }, Ct);

        devices.GetArrayLength().ShouldBe(11);
        var list = _cloud.Requests("GET", "/devices").Single();
        FakeLgCloud.Header(list, "Authorization").ShouldBe($"Bearer {Pat}");
        FakeLgCloud.Header(list, "x-country").ShouldBe("BR");
        FakeLgCloud.Header(list, "x-client-id").ShouldBe(ClientId);
        FakeLgCloud.Header(list, "x-api-key").ShouldBe(LgThinQSettings.DefaultApiKey);
        FakeLgCloud.Header(list, "x-service-phase").ShouldBe("OP");
        FakeLgCloud.Header(list, "x-message-id").Length.ShouldBe(22);
        FakeLgCloud.HasHeader(list, "x-conditional-control").ShouldBeFalse();

        var control = _cloud.Requests("POST", "/devices/test-ac-0001/control").Single();
        FakeLgCloud.Header(control, "x-conditional-control").ShouldBe("true");
        FakeLgCloud.Header(control, "x-message-id").ShouldNotBe(FakeLgCloud.Header(list, "x-message-id"));
        control.Body.ShouldBe("""{"operation":{"airConOperationMode":"POWER_ON"}}""");
    }

    [Theory]
    [InlineData("1103", 401, nameof(LgErrorKind.NeedsReauth))]
    [InlineData("1218", 401, nameof(LgErrorKind.NeedsReauth))]
    [InlineData("1304", 400, nameof(LgErrorKind.Account))]
    [InlineData("1307", 400, nameof(LgErrorKind.Account))]
    [InlineData("2301", 400, nameof(LgErrorKind.CommandRejected))]
    public async Task Error_codes_come_back_classified(string code, int status, string kind)
    {
        _cloud.Fails("GET", "/devices", code, status);
        using var factory = Factory();

        var error = await Should.ThrowAsync<LgApiException>(Api(factory).GetDevicesAsync(Ct));

        (error.Code, error.Kind, (int?)error.Status).ShouldBe((code, Enum.Parse<LgErrorKind>(kind), status));
        error.Message.ShouldNotContain(Pat);
    }

    [Fact]
    public async Task A_1306_pauses_every_call_of_the_token_for_an_hour_without_reaching_lg()
    {
        _cloud.Fails("GET", "/devices", LgErrors.ExceededApiCalls, 429);
        using var factory = Factory();
        var api = Api(factory);

        (await Should.ThrowAsync<LgApiException>(api.GetDevicesAsync(Ct))).Kind.ShouldBe(LgErrorKind.QuotaExceeded);
        var blocked = await Should.ThrowAsync<LgQuotaBlockedException>(api.GetStateAsync("test-ac-0001", Ct));

        blocked.Until.ShouldBe(_time.GetUtcNow().AddHours(1));
        _cloud.Requests().Count.ShouldBe(1); // 429 is not retried, and the second call never left

        // Another integration with the same token shares the pause.
        await Should.ThrowAsync<LgQuotaBlockedException>(Api(factory, clientId: "controlhome-other").GetDevicesAsync(Ct));
    }

    [Fact]
    public async Task Already_subscribed_counts_as_subscribed()
    {
        _cloud.Fails("POST", "/push/test-ac-0001/subscribe", LgErrors.AlreadySubscribedPush);
        _cloud.Ok("POST", "/event/test-ac-0001/subscribe", "{}");
        using var factory = Factory();
        var api = Api(factory);

        await api.SubscribePushAsync("test-ac-0001", Ct);
        await api.SubscribeEventAsync("test-ac-0001", Ct);

        FakeLgCloud.Body(_cloud.Requests("POST", "/event/test-ac-0001/subscribe").Single()).GetRawText()
            .ShouldBe("""{"expire":{"unit":"HOUR","timer":4464}}""");
    }

    [Fact]
    public async Task Gets_are_retried_after_a_server_error_but_controls_never()
    {
        _cloud.Server.Given(Request.Create().WithPath("/devices/test-ac-0001/state").UsingGet()).InScenario("flaky").WillSetStateTo("recovered")
            .RespondWith(Response.Create().WithStatusCode(503).WithBody(LgFixtures.Error("2000")));
        _cloud.Server.Given(Request.Create().WithPath("/devices/test-ac-0001/state").UsingGet()).InScenario("flaky").WhenStateIs("recovered")
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(LgFixtures.Ok("""{"operation":{"airConOperationMode":"POWER_ON"}}""")));
        _cloud.Fails("POST", "/devices/test-ac-0001/control", "2000", 503);
        using var factory = Factory();
        var api = Api(factory);

        var state = await api.GetStateAsync("test-ac-0001", Ct);
        var control = await Should.ThrowAsync<LgApiException>(api.ControlAsync("test-ac-0001", new JsonObject { ["operation"] = new JsonObject() }, Ct));

        state.GetProperty("operation").GetProperty("airConOperationMode").GetString().ShouldBe("POWER_ON");
        _cloud.Requests("GET", "/devices/test-ac-0001/state").Count.ShouldBe(2);
        control.Kind.ShouldBe(LgErrorKind.Transient);
        _cloud.Requests("POST", "/devices/test-ac-0001/control").Count.ShouldBe(1); // at-most-once
    }

    [Fact]
    public async Task Waiting_for_the_call_budget_does_not_count_against_the_attempt_timeout()
    {
        _cloud.Ok("POST", "/devices/test-ac-0001/control", "{}");
        using var factory = new LgThinQConnectorFactory(null, _cloud.Options() with { Rate = new LgRateOptions(1, TimeSpan.FromSeconds(10), TimeSpan.FromHours(1)) }, null);
        var api = Api(factory);

        // Three commands at once with one call every 10 s: the third waits 20 s for its turn, longer than the 15 s an
        // attempt may take, and still goes out (a control call is never retried, so a timeout would lose it).
        var calls = Enumerable.Range(0, 3)
            .Select(_ => api.ControlAsync("test-ac-0001", new JsonObject { ["operation"] = new JsonObject { ["airConOperationMode"] = "POWER_ON" } }, Ct))
            .ToList();
        await Waiting.UntilAsync(() => Controls() == 1, "the first command went out");
        _time.Advance(TimeSpan.FromSeconds(10));
        await Waiting.UntilAsync(() => Controls() == 2, "the second command went out");
        _time.Advance(TimeSpan.FromSeconds(10));

        await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Controls().ShouldBe(3);

        int Controls() => _cloud.Requests("POST", "/devices/test-ac-0001/control").Count;
    }

    [Fact]
    public async Task A_retry_after_header_never_parks_a_get_retry()
    {
        _cloud.Server.Given(Request.Create().WithPath("/devices/test-ac-0001/state").UsingGet()).InScenario("busy").WillSetStateTo("recovered")
            .RespondWith(Response.Create().WithStatusCode(503).WithHeader("Retry-After", "3600").WithBody(LgFixtures.Error("2000")));
        _cloud.Server.Given(Request.Create().WithPath("/devices/test-ac-0001/state").UsingGet()).InScenario("busy").WhenStateIs("recovered")
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(LgFixtures.Ok("""{"operation":{"airConOperationMode":"POWER_ON"}}""")));
        using var factory = Factory();

        // The test options retry without delay; honouring "Retry-After: 3600" would park the retry for an hour of the clock.
        var state = Api(factory).GetStateAsync("test-ac-0001", Ct);
        await Waiting.UntilAsync(() => _cloud.Requests("GET", "/devices/test-ac-0001/state").Count >= 1, "the first attempt failed");
        _time.Advance(TimeSpan.FromSeconds(30));

        (await state.WaitAsync(TimeSpan.FromSeconds(10), Ct)).GetProperty("operation").GetProperty("airConOperationMode").GetString().ShouldBe("POWER_ON");
        _cloud.Requests("GET", "/devices/test-ac-0001/state").Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_mqtt_client_calls_use_the_sdks_bodies()
    {
        _cloud.Account();
        using var factory = Factory();
        var api = Api(factory);

        await api.RegisterClientAsync(Ct);
        var (_, csr) = ControlHome.Connectors.LgThinQ.Mqtt.LgCsr.Create();
        var issued = await api.IssueCertificateAsync(csr, Ct);
        var route = await api.GetRouteAsync(Ct);

        FakeLgCloud.Body(_cloud.Requests("POST", "/client").Single()).GetRawText()
            .ShouldBe("""{"type":"MQTT","service-code":"SVC202","device-type":"607","allowExist":true}""");
        var certificateRequest = FakeLgCloud.Body(_cloud.Requests("POST", "/client/certificate").Single());
        certificateRequest.GetProperty("service-code").GetString().ShouldBe("SVC202");
        certificateRequest.GetProperty("csr").GetString()!.ShouldNotContain("-----"); // base64 DER without PEM armour
        issued.GetProperty("result").GetProperty("subscriptions")[0].GetString().ShouldBe(FakeLgCloud.Topic);
        route.GetProperty("mqttServer").GetString().ShouldBe("mqtts://broker.test:8883");
    }

    [Fact]
    public void The_country_picks_the_region()
    {
        LgCountries.BaseUrlOf("BR").ShouldBe(new Uri("https://api-aic.lgthinq.com/"));
        LgCountries.BaseUrlOf("kr").ShouldBe(new Uri("https://api-kic.lgthinq.com/"));
        LgCountries.BaseUrlOf("DE").ShouldBe(new Uri("https://api-eic.lgthinq.com/"));
        LgCountries.RegionOf("ZZ").ShouldBeNull();
    }

    private LgThinQConnectorFactory Factory() => new(null, _cloud.Options(), null);

    private LgApiClient Api(LgThinQConnectorFactory factory, string clientId = ClientId) =>
        factory.CreateApi(LgThinQSettings.Parse(LgFixtures.Parse("""{"country":"BR"}"""), TimeZoneInfo.Utc), Pat, clientId, _time, NullLogger.Instance);
}

using System.Text.Json;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.DahuaHttp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ControlHome.Connectors.Tests.DahuaHttp;

/// <summary>The setup flow against a WireMock device with real Digest authentication.</summary>
public sealed class DahuaSetupFlowTests : IDisposable
{
    private readonly FakeDahuaDevice _device = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _device.Dispose();

    [Fact]
    public async Task The_form_starts_with_defaults_in_the_home_language()
    {
        await using var flow = DahuaHarness.Factory().CreateSetupFlow()!;

        var form = (FormStep)await flow.StartAsync(Context(language: "pt-BR"), Ct);

        form.StepId.ShouldBe(DahuaSetupFlow.DeviceStep);
        form.Values!.Value.GetProperty("port").GetInt32().ShouldBe(80);
        form.Values!.Value.GetProperty("username").GetString().ShouldBe("admin");
        form.Values!.Value.GetProperty("kind").GetString().ShouldBe("auto");
        form.Values!.Value.GetProperty("language").GetString().ShouldBe("pt-BR");
        form.Fields.Single(f => f.Key == "password").Secret.ShouldBeTrue();
        form.Fields.Single(f => f.Key == "kind").Schema.ShouldBeOfType<Domain.Capabilities.EnumSchema>().Values.ShouldNotContain("xpe");
        form.Title.PtBr.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_camera_is_identified_with_digest_auth_and_finishes_with_the_password_as_a_secret()
    {
        _device.Identity("VIP 1230 B G4");
        _device.Answer("/cgi-bin/configManager.cgi", "getConfig", ("name", "ChannelTitle")).WithBody("table.ChannelTitle[0].Name=Entrada\r\n");
        await using var flow = DahuaHarness.Factory().CreateSetupFlow()!;
        await flow.StartAsync(Context(), Ct);

        var done = (await flow.ContinueAsync(DahuaSetupFlow.DeviceStep, Input(), Ct)).ShouldBeOfType<DoneStep>();

        done.Name.ShouldBe("Entrada (127.0.0.1)");
        done.Config.GetProperty("kind").GetString().ShouldBe("camera");
        done.Config.TryGetProperty("password", out _).ShouldBeFalse();
        done.Config.GetRawText().ShouldNotContain(FakeDahuaDevice.Password);
        done.Secrets.ShouldBe(new Dictionary<string, string> { ["password"] = FakeDahuaDevice.Password });
        JsonSerializer.Serialize<FlowStep>(done).ShouldNotContain("synthetic");

        // Digest: every CGI was first challenged, then answered to a valid Digest response for the password (never Basic).
        var typeRequests = _device.All("/cgi-bin/magicBox.cgi", "getDeviceType");
        typeRequests.Count.ShouldBe(2);
        FakeDahuaDevice.Header(typeRequests[0], "Authorization").ShouldBeNull();
        DahuaFixtures.IsValidDigest(FakeDahuaDevice.Header(typeRequests[1], "Authorization")!, "GET", FakeDahuaDevice.User, FakeDahuaDevice.Password).ShouldBeTrue();
        _device.Requests.Select(r => FakeDahuaDevice.Header(r, "Authorization")).OfType<string>().ShouldAllBe(h => h.StartsWith("Digest "));
        _device.Authenticated("/cgi-bin/magicBox.cgi", "getSoftwareVersion").ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("NVD 1404", "recorder")]
    [InlineData("SS 3530 MF FACE", "accessControl")]
    public async Task The_device_kind_is_detected(string type, string kind)
    {
        _device.Identity(type, name: null);
        _device.Answer("/cgi-bin/configManager.cgi", "getConfig", ("name", "ChannelTitle")).WithBody(DahuaFixtures.Text("channel-title-recorder.txt"));
        await using var flow = DahuaHarness.Factory().CreateSetupFlow()!;
        await flow.StartAsync(Context(), Ct);

        var done = (await flow.ContinueAsync(DahuaSetupFlow.DeviceStep, Input(), Ct)).ShouldBeOfType<DoneStep>();

        done.Config.GetProperty("kind").GetString().ShouldBe(kind);
        done.Name.ShouldBe($"{type} (127.0.0.1)");
    }

    [Fact]
    public async Task A_refused_login_shows_the_checklist_and_is_never_retried_on_its_own()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-21T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        _device.RejectCredentials();
        await using var flow = DahuaHarness.Factory().CreateSetupFlow()!;
        await flow.StartAsync(Context(time), Ct);

        var checklist = (await flow.ContinueAsync(DahuaSetupFlow.DeviceStep, Input(), Ct)).ShouldBeOfType<ChooseStep>();

        checklist.StepId.ShouldBe(DahuaSetupFlow.ChecklistStep);
        checklist.Description!.En.ShouldContain("Compatibility");
        checklist.Description!.En.ShouldContain("MD5");
        checklist.Description!.PtBr.ShouldContain("Compatível");
        checklist.Choices.Select(c => c.Key).ShouldBe([DahuaSetupFlow.RetryChoice, DahuaSetupFlow.EditChoice]);
        _device.Authenticated("/cgi-bin/magicBox.cgi", "getDeviceType").ShouldHaveSingleItem("one failed login, nothing retried");

        // An immediate retry (a double click) does not reach the device.
        var tooSoon = (await flow.ContinueAsync(DahuaSetupFlow.ChecklistStep, Choice("retry"), Ct)).ShouldBeOfType<ChooseStep>();
        tooSoon.Description!.En.ShouldContain("Wait 30 s");
        _device.Authenticated("/cgi-bin/magicBox.cgi", "getDeviceType").ShouldHaveSingleItem();

        time.Advance(TimeSpan.FromSeconds(31));
        (await flow.ContinueAsync(DahuaSetupFlow.ChecklistStep, Choice("retry"), Ct)).ShouldBeOfType<ChooseStep>();
        _device.Authenticated("/cgi-bin/magicBox.cgi", "getDeviceType").Count.ShouldBe(2);

        time.Advance(TimeSpan.FromSeconds(31));
        var abort = (await flow.ContinueAsync(DahuaSetupFlow.ChecklistStep, Choice("retry"), Ct)).ShouldBeOfType<AbortStep>();
        abort.Reason.ShouldBe("too_many_failed_logins");
        abort.Message.PtBr.ShouldContain("30 minutos");
        _device.Authenticated("/cgi-bin/magicBox.cgi", "getDeviceType").Count.ShouldBe(3);
    }

    [Fact]
    public async Task Resubmitting_refused_credentials_right_away_does_not_reach_the_device()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-21T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        _device.RejectCredentials();
        await using var flow = DahuaHarness.Factory().CreateSetupFlow()!;
        await flow.StartAsync(Context(time), Ct);
        (await flow.ContinueAsync(DahuaSetupFlow.DeviceStep, Input(), Ct)).ShouldBeOfType<ChooseStep>();
        (await flow.ContinueAsync(DahuaSetupFlow.ChecklistStep, Choice("edit"), Ct)).ShouldBeOfType<FormStep>();

        // "Change the settings", then the same values again at once (a double click, nothing corrected): not sent.
        var tooSoon = (await flow.ContinueAsync(DahuaSetupFlow.DeviceStep, Input(), Ct)).ShouldBeOfType<ChooseStep>();
        tooSoon.Description!.En.ShouldContain("Wait 30 s");
        _device.Authenticated("/cgi-bin/magicBox.cgi", "getDeviceType").ShouldHaveSingleItem();

        // A corrected password is tried at once.
        var corrected = DahuaFixtures.Json($$"""{"host":"127.0.0.1","port":{{_device.Port}},"username":"{{FakeDahuaDevice.User}}","password":"another-synthetic-pass","kind":"auto"}""");
        (await flow.ContinueAsync(DahuaSetupFlow.DeviceStep, corrected, Ct)).ShouldBeOfType<ChooseStep>();
        _device.Authenticated("/cgi-bin/magicBox.cgi", "getDeviceType").Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(500, "Compatibility")]
    [InlineData(404, "CGI")]
    [InlineData(403, "IP filter")]
    public async Task Other_refusals_explain_their_likely_cause(int status, string hint)
    {
        _device.Answer("/cgi-bin/magicBox.cgi", "getDeviceType").WithStatusCode(status).WithBody("Error\r\n");
        await using var flow = DahuaHarness.Factory().CreateSetupFlow()!;
        await flow.StartAsync(Context(), Ct);

        var checklist = (await flow.ContinueAsync(DahuaSetupFlow.DeviceStep, Input(), Ct)).ShouldBeOfType<ChooseStep>();

        checklist.Description!.En.ShouldContain(hint);
        var form = (await flow.ContinueAsync(DahuaSetupFlow.ChecklistStep, Choice("edit"), Ct)).ShouldBeOfType<FormStep>();
        form.Values!.Value.GetProperty("host").GetString().ShouldBe("127.0.0.1");
        form.Values!.Value.TryGetProperty("password", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task An_unreachable_device_or_invalid_values_are_field_errors()
    {
        var port = _device.Port;
        _device.Dispose();
        await using var flow = DahuaHarness.Factory().CreateSetupFlow()!;
        await flow.StartAsync(Context(), Ct);

        var unreachable = (await flow.ContinueAsync(DahuaSetupFlow.DeviceStep, Input(port), Ct)).ShouldBeOfType<FormStep>();
        unreachable.Errors.Keys.ShouldBe(["host"]);
        unreachable.Errors["host"].PtBr.ShouldNotBeNullOrWhiteSpace();

        var invalid = (await flow.ContinueAsync(DahuaSetupFlow.DeviceStep, DahuaFixtures.Json("""{"host":"http://x/","username":"a","password":"b"}"""), Ct)).ShouldBeOfType<FormStep>();
        invalid.Errors.Keys.ShouldBe(["host"]);

        var missing = (await flow.ContinueAsync(DahuaSetupFlow.DeviceStep, DahuaFixtures.Json("""{"host":"192.0.2.10","username":"a"}"""), Ct)).ShouldBeOfType<FormStep>();
        missing.Errors.Keys.ShouldBe(["password"]);

        var badChannels = (await flow.ContinueAsync(DahuaSetupFlow.DeviceStep, DahuaFixtures.Json("""{"host":"192.0.2.10","username":"a","password":"b","channels":"9-1"}"""), Ct)).ShouldBeOfType<FormStep>();
        badChannels.Errors.Keys.ShouldBe(["channels"]);
    }

    [Fact]
    public async Task An_existing_integration_keeps_its_password_only_for_the_same_device()
    {
        _device.Identity("VIP 1230 B G4");
        var existing = DahuaHarness.Instance(DahuaHarness.Config(_device.Port, "camera"));
        await using var flow = DahuaHarness.Factory().CreateSetupFlow()!;

        var form = (FormStep)await flow.StartAsync(Context(existing: existing), Ct);
        form.Values!.Value.GetProperty("kind").GetString().ShouldBe("camera");

        var done = (await flow.ContinueAsync(DahuaSetupFlow.DeviceStep, DahuaHarness.Config(_device.Port, "camera"), Ct)).ShouldBeOfType<DoneStep>();
        done.Name.ShouldBe("Test");
        done.Secrets["password"].ShouldBe(FakeDahuaDevice.Password);

        var elsewhere = DahuaFixtures.Json($$"""{"host":"127.0.0.2","port":{{_device.Port}},"username":"{{FakeDahuaDevice.User}}","kind":"camera"}""");
        (await flow.ContinueAsync(DahuaSetupFlow.DeviceStep, elsewhere, Ct)).ShouldBeOfType<FormStep>().Errors.Keys.ShouldBe(["password"]);
    }

    [Fact]
    public async Task The_xpe_relay_is_offered_only_behind_its_flag_and_only_probed()
    {
        _device.Server.Given(WireMock.RequestBuilders.Request.Create().UsingGet().WithPath("/"))
            .AtPriority(1)
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(200).WithBody("<html></html>"));
        await using (var off = DahuaHarness.Factory().CreateSetupFlow()!)
        {
            await off.StartAsync(Context(), Ct);
            (await off.ContinueAsync(DahuaSetupFlow.DeviceStep, Input(kind: "xpe"), Ct)).ShouldBeOfType<FormStep>().Errors.Keys.ShouldBe(["kind"]);
        }

        await using var on = DahuaHarness.Factory(xpeRelay: true).CreateSetupFlow()!;
        await on.StartAsync(Context(), Ct);
        var done = (await on.ContinueAsync(DahuaSetupFlow.DeviceStep, Input(kind: "xpe"), Ct)).ShouldBeOfType<DoneStep>();

        done.Config.GetProperty("kind").GetString().ShouldBe("xpe");
        _device.Requests.ShouldAllBe(r => r.RequestMessage!.Path == "/", "the relay itself is never triggered by the setup");
        _device.Requests.ShouldAllBe(r => FakeDahuaDevice.Header(r, "Authorization") == null);
    }

    private JsonElement Input(int? port = null, string kind = "auto") =>
        DahuaFixtures.Json($$"""{"host":"127.0.0.1","port":{{port ?? _device.Port}},"username":"{{FakeDahuaDevice.User}}","password":"{{FakeDahuaDevice.Password}}","kind":"{{kind}}"}""");

    private static JsonElement Choice(string choice) => DahuaFixtures.Json($$"""{"choice":"{{choice}}"}""");

    private static FlowContext Context(TimeProvider? time = null, IntegrationInstance? existing = null, string language = "en") =>
        new(time ?? TimeProvider.System, NullLogger.Instance, existing) { Language = language };
}

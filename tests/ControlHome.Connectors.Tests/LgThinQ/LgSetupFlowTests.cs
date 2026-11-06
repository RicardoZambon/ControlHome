using System.Text.Json;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.LgThinQ;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ControlHome.Connectors.Tests.LgThinQ;

/// <summary>The setup and replace-token flow: the token is checked with GET devices; secrets never reach the config or the form.</summary>
public sealed class LgSetupFlowTests : IDisposable
{
    private const string Pat = "test-pat-not-a-real-token";

    private readonly FakeLgCloud _cloud = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _cloud.Dispose();

    [Fact]
    public async Task Setup_checks_the_token_and_saves_it_with_a_new_persistent_client_id()
    {
        _cloud.Ok("GET", "/devices", LgFixtures.Text("devices.json"));
        using var factory = new LgThinQConnectorFactory(null, _cloud.Options(), null);
        await using var flow = factory.CreateSetupFlow()!;

        var form = (await flow.StartAsync(Context(), Ct)).ShouldBeOfType<FormStep>();
        form.Fields.Select(f => f.Key).ShouldBe(["name", "pat", "country", "energy", "timeZone", "apiKey"]);
        form.Fields.Single(f => f.Key == "pat").Secret.ShouldBeTrue();
        form.Values!.Value.GetProperty("country").GetString().ShouldBe("BR");
        form.Description!.En.ShouldContain("https://connect-pat.lgthinq.com");
        form.Description.PtBr.ShouldContain("consulta de consumo de energia");

        var done = (await flow.ContinueAsync(form.StepId, Input($$"""{"name":"Casa","pat":"{{Pat}}","country":"BR","energy":true}"""), Ct))
            .ShouldBeOfType<DoneStep>();

        done.Name.ShouldBe("Casa");
        done.Config.GetRawText().ShouldBe("""{"country":"BR","energy":true}""");
        done.Secrets[LgSecrets.Pat].ShouldBe(Pat);
        done.Secrets[LgSecrets.ClientId].ShouldStartWith("controlhome-");
        Guid.TryParse(done.Secrets[LgSecrets.ClientId]["controlhome-".Length..], out _).ShouldBeTrue();
        JsonSerializer.Serialize<FlowStep>(done).ShouldNotContain(Pat);
        var request = _cloud.Requests("GET", "/devices").Single();
        FakeLgCloud.Header(request, "x-client-id").ShouldBe(done.Secrets[LgSecrets.ClientId]);
        FakeLgCloud.Header(request, "x-country").ShouldBe("BR");
    }

    [Theory]
    [InlineData("1103", "pat", "rejected this token")]
    [InlineData("1304", "pat", "terms")]
    [InlineData("1307", "country", "country")]
    [InlineData("1306", "pat", "call limit")]
    [InlineData("1301", "apiKey", "API key")]
    public async Task Rejections_come_back_on_the_field_to_fix(string code, string field, string text)
    {
        _cloud.Fails("GET", "/devices", code, code == "1103" ? 401 : 400);
        using var factory = new LgThinQConnectorFactory(null, _cloud.Options(), null);
        await using var flow = factory.CreateSetupFlow()!;
        var form = (FormStep)await flow.StartAsync(Context(), Ct);

        var again = (await flow.ContinueAsync(form.StepId, Input($$"""{"name":"LG","pat":"{{Pat}}","country":"US"}"""), Ct)).ShouldBeOfType<FormStep>();

        again.Errors.Keys.ShouldBe([field]);
        again.Errors[field].En.ShouldContain(text);
        again.Values!.Value.TryGetProperty("pat", out _).ShouldBeFalse(); // the token is never echoed
        again.Values.Value.GetProperty("country").GetString().ShouldBe("US");
    }

    [Fact]
    public async Task A_new_integration_needs_a_token_and_a_known_time_zone()
    {
        using var factory = new LgThinQConnectorFactory(null, _cloud.Options(), null);
        await using var flow = factory.CreateSetupFlow()!;
        var form = (FormStep)await flow.StartAsync(Context(), Ct);

        var missing = (FormStep)await flow.ContinueAsync(form.StepId, Input("""{"name":"LG","country":"BR"}"""), Ct);
        var zone = (FormStep)await flow.ContinueAsync(form.StepId, Input($$"""{"name":"LG","pat":"{{Pat}}","timeZone":"Mars/Olympus"}"""), Ct);

        missing.Errors.Keys.ShouldBe(["pat"]);
        zone.Errors.Keys.ShouldBe(["timeZone"]);
        _cloud.Requests().ShouldBeEmpty();
    }

    [Fact]
    public async Task Replacing_the_token_keeps_the_client_id_and_the_mqtt_certificate()
    {
        _cloud.Ok("GET", "/devices", LgFixtures.Text("devices.json"));
        using var factory = new LgThinQConnectorFactory(null, _cloud.Options(), null);
        var secrets = new MemorySecretStore(
            (LgSecrets.Pat, "test-pat-old"),
            (LgSecrets.ClientId, "controlhome-00000000-0000-4000-8000-0000000000aa"),
            (LgSecrets.MqttCredentials, """{"key":"k","cert":"c","topic":"t","issuedAt":"2026-10-01T00:00:00+00:00"}"""));
        var existing = new IntegrationInstance(Guid.NewGuid(), "Minha casa", LgFixtures.Parse("""{"country":"BR","energy":false}"""), secrets);
        await using var flow = factory.CreateSetupFlow()!;

        var form = (await flow.StartAsync(Context(existing), Ct)).ShouldBeOfType<FormStep>();
        form.Title.En.ShouldBe("Replace the LG ThinQ token");
        form.Values!.Value.GetProperty("name").GetString().ShouldBe("Minha casa");
        form.Values.Value.TryGetProperty("pat", out _).ShouldBeFalse();

        var done = (await flow.ContinueAsync(form.StepId, Input($$"""{"name":"Minha casa","pat":"{{Pat}}","country":"BR","energy":false}"""), Ct))
            .ShouldBeOfType<DoneStep>();

        done.Secrets[LgSecrets.Pat].ShouldBe(Pat);
        done.Secrets[LgSecrets.ClientId].ShouldBe("controlhome-00000000-0000-4000-8000-0000000000aa");
        done.Secrets[LgSecrets.MqttCredentials].ShouldBe(secrets[LgSecrets.MqttCredentials]);
        done.Config.GetRawText().ShouldBe("""{"country":"BR","energy":false}""");
        FakeLgCloud.Header(_cloud.Requests("GET", "/devices").Single(), "Authorization").ShouldBe($"Bearer {Pat}");

        // A blank token keeps the stored one (settings change only).
        await using var again = factory.CreateSetupFlow()!;
        var form2 = (FormStep)await again.StartAsync(Context(existing), Ct);
        var kept = (await again.ContinueAsync(form2.StepId, Input("""{"name":"Minha casa","pat":"","country":"BR","energy":true}"""), Ct)).ShouldBeOfType<DoneStep>();
        kept.Secrets[LgSecrets.Pat].ShouldBe("test-pat-old");
    }

    [Fact]
    public void A_runtime_is_created_without_starting_anything_and_bad_configs_are_refused()
    {
        using var factory = new LgThinQConnectorFactory(null, _cloud.Options(), null);

        var runtime = factory.CreateRuntime(new IntegrationInstance(Guid.NewGuid(), "LG", LgFixtures.Parse("""{"country":"BR"}"""), EmptySecretStore.Instance));

        runtime.ShouldNotBeNull();
        _cloud.Requests().ShouldBeEmpty();
        Should.Throw<ArgumentException>(() => factory.CreateRuntime(new IntegrationInstance(Guid.NewGuid(), "LG", LgFixtures.Parse("""{"country":"ZZ"}"""), EmptySecretStore.Instance)));
        Should.Throw<ArgumentException>(() => factory.CreateRuntime(new IntegrationInstance(Guid.NewGuid(), "LG", LgFixtures.Parse("""{"unknown":1}"""), EmptySecretStore.Instance)));
        LgThinQSettings.Parse(LgFixtures.Parse("""{"country":"BR"}"""), TimeZoneInfo.Utc).TimeZone.Id.ShouldBe("America/Sao_Paulo");
        LgThinQSettings.Parse(LgFixtures.Parse("""{"country":"US","timeZone":"America/Chicago","apiKey":"custom-key"}"""), TimeZoneInfo.Utc)
            .ShouldSatisfyAllConditions(s => s.TimeZone.Id.ShouldBe("America/Chicago"), s => s.ApiKey.ShouldBe("custom-key"), s => s.Region.ShouldBe("aic"));
    }

    private FlowContext Context(IntegrationInstance? existing = null) => new(_time, NullLogger.Instance, existing);

    private static JsonElement Input(string json) => LgFixtures.Parse(json);
}

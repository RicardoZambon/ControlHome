using System.Text.Json;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.Mqtt;
using ControlHome.Connectors.Mqtt.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ControlHome.Connectors.Tests.Mqtt;

/// <summary>The setup / settings / permit-join flow against an in-memory broker.</summary>
public sealed class MqttSetupFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Setup_shows_the_connection_form_with_defaults()
    {
        await using var flow = Factory(new FakeBroker()).CreateSetupFlow()!;

        var step = (FormStep)await flow.StartAsync(Context(), Ct);

        step.StepId.ShouldBe(MqttSetupFlow.ConnectionStep);
        step.Fields.ShouldBe(MqttConnectorDescriptor.Instance.ConfigFields);
        step.Values!.Value.GetProperty("port").GetInt32().ShouldBe(1883);
        step.Values!.Value.GetProperty("discoveryPrefix").GetString().ShouldBe("homeassistant");
        step.Values!.Value.GetProperty("zigbee2mqttBaseTopic").GetString().ShouldBe("zigbee2mqtt");
        step.Errors.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_successful_connection_test_finishes_with_the_password_as_a_secret()
    {
        var broker = new FakeBroker { Credentials = ("ch", "s3cret-value") };
        await using var flow = Factory(broker).CreateSetupFlow()!;
        await flow.StartAsync(Context(), Ct);

        var step = await flow.ContinueAsync(
            MqttSetupFlow.ConnectionStep,
            MqttFixtures.Parse("""{"host":"umbrel.local","port":1883,"username":"ch","password":"s3cret-value","zigbee2mqttBaseTopic":"zigbee2mqtt"}"""),
            Ct);

        var done = step.ShouldBeOfType<DoneStep>();
        done.Name.ShouldBe("MQTT (umbrel.local)");
        done.Config.GetRawText().ShouldNotContain("s3cret-value");
        done.Config.TryGetProperty("password", out _).ShouldBeFalse();
        done.Config.GetProperty("host").GetString().ShouldBe("umbrel.local");
        done.Secrets.ShouldBe(new Dictionary<string, string> { ["password"] = "s3cret-value" });
        JsonSerializer.Serialize<FlowStep>(done).ShouldNotContain("s3cret-value");

        // The test used a throw-away client id (the runtime's own id must not be kicked off the broker).
        broker.Attempts.ShouldHaveSingleItem().ClientId.ShouldStartWith("controlhome-setup-");
        broker.Clients.ShouldBeEmpty();
    }

    [Fact]
    public async Task Wrong_credentials_return_the_form_with_a_password_error_and_no_password()
    {
        var broker = new FakeBroker { Credentials = ("ch", "right") };
        await using var flow = Factory(broker).CreateSetupFlow()!;
        await flow.StartAsync(Context(), Ct);

        var step = await flow.ContinueAsync(MqttSetupFlow.ConnectionStep, MqttFixtures.Parse("""{"host":"umbrel.local","username":"ch","password":"wrong"}"""), Ct);

        var form = step.ShouldBeOfType<FormStep>();
        form.Errors.Keys.ShouldBe(["password"]);
        form.Errors["password"].PtBr.ShouldNotBeNullOrWhiteSpace();
        form.Values!.Value.GetProperty("host").GetString().ShouldBe("umbrel.local");
        form.Values!.Value.TryGetProperty("password", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task An_unreachable_broker_or_invalid_values_are_field_errors()
    {
        var broker = new FakeBroker();
        await broker.StopAsync();
        await using var flow = Factory(broker).CreateSetupFlow()!;
        await flow.StartAsync(Context(), Ct);

        var unreachable = (FormStep)await flow.ContinueAsync(MqttSetupFlow.ConnectionStep, MqttFixtures.Parse("""{"host":"10.0.0.9","port":1884}"""), Ct);
        unreachable.Errors.Keys.ShouldBe(["host"]);
        unreachable.Errors["host"].En.ShouldContain("10.0.0.9:1884");

        var missing = (FormStep)await flow.ContinueAsync(MqttSetupFlow.ConnectionStep, MqttFixtures.Parse("""{"port":1883}"""), Ct);
        missing.Errors.Keys.ShouldBe(["host"]);

        var wildcard = (FormStep)await flow.ContinueAsync(MqttSetupFlow.ConnectionStep, MqttFixtures.Parse("""{"host":"b","zigbee2mqttBaseTopic":"z/#"}"""), Ct);
        wildcard.Errors.Keys.ShouldBe(["zigbee2mqttBaseTopic"]);
        broker.Attempts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task For_an_existing_integration_the_settings_keep_the_stored_password_when_left_blank()
    {
        var broker = new FakeBroker { Credentials = ("ch", "stored-secret") };
        await using var flow = Factory(broker).CreateSetupFlow()!;
        var existing = Existing("""{"host":"umbrel.local","username":"ch"}""", "stored-secret");

        var choose = (ChooseStep)await flow.StartAsync(Context(existing), Ct);
        choose.Choices.Select(c => c.Key).ShouldBe([MqttSetupFlow.SettingsChoice, MqttSetupFlow.PermitJoinChoice]);
        var form = (FormStep)await flow.ContinueAsync(MqttSetupFlow.ActionStep, MqttFixtures.Parse("""{"choice":"settings"}"""), Ct);
        form.Values!.Value.GetProperty("host").GetString().ShouldBe("umbrel.local");

        var done = (DoneStep)await flow.ContinueAsync(MqttSetupFlow.ConnectionStep, MqttFixtures.Parse("""{"host":"umbrel.local","username":"ch","port":1883}"""), Ct);

        done.Name.ShouldBe("My MQTT");
        done.Secrets["password"].ShouldBe("stored-secret");
    }

    [Theory]
    [InlineData("""{"host":"attacker.example","username":"ch"}""")]
    [InlineData("""{"host":"umbrel.local","port":1884,"username":"ch"}""")]
    [InlineData("""{"host":"umbrel.local","username":"someone-else"}""")]
    [InlineData("""{"host":"umbrel.local","username":"ch","useTls":true}""")]
    public async Task The_stored_password_is_never_sent_to_another_broker_or_user(string changed)
    {
        // Admins never see the stored password: pointing the settings at another host (or port, user, TLS mode) with the
        // password left blank must not hand it to that broker. The connection is tested without it instead.
        var broker = new FakeBroker();
        await using var flow = Factory(broker).CreateSetupFlow()!;
        var existing = Existing("""{"host":"umbrel.local","username":"ch"}""", "stored-secret");
        await flow.StartAsync(Context(existing), Ct);
        await flow.ContinueAsync(MqttSetupFlow.ActionStep, MqttFixtures.Parse("""{"choice":"settings"}"""), Ct);

        var done = (DoneStep)await flow.ContinueAsync(MqttSetupFlow.ConnectionStep, MqttFixtures.Parse(changed), Ct);

        broker.Attempts.ShouldHaveSingleItem().Password.ShouldBeNull();
        done.Secrets.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_broker_that_needs_the_password_asks_for_it_again_after_the_host_changed()
    {
        var broker = new FakeBroker { Credentials = ("ch", "stored-secret") };
        await using var flow = Factory(broker).CreateSetupFlow()!;
        await flow.StartAsync(Context(Existing("""{"host":"umbrel.local","username":"ch"}""", "stored-secret")), Ct);
        await flow.ContinueAsync(MqttSetupFlow.ActionStep, MqttFixtures.Parse("""{"choice":"settings"}"""), Ct);

        var form = (FormStep)await flow.ContinueAsync(MqttSetupFlow.ConnectionStep, MqttFixtures.Parse("""{"host":"192.168.1.10","username":"ch"}"""), Ct);

        form.Errors.Keys.ShouldBe(["password"]);
        var done = (DoneStep)await flow.ContinueAsync(
            MqttSetupFlow.ConnectionStep,
            MqttFixtures.Parse("""{"host":"192.168.1.10","username":"ch","password":"stored-secret"}"""),
            Ct);
        done.Secrets["password"].ShouldBe("stored-secret");
    }

    [Fact]
    public async Task Permit_join_publishes_the_request_and_waits_until_the_window_closes()
    {
        var broker = new FakeBroker { Credentials = ("ch", "stored-secret") };
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var flow = Factory(broker).CreateSetupFlow()!;
        var existing = Existing("""{"host":"umbrel.local","username":"ch","zigbee2mqttBaseTopic":"z2m"}""", "stored-secret");
        await flow.StartAsync(Context(existing, time), Ct);
        var form = (FormStep)await flow.ContinueAsync(MqttSetupFlow.ActionStep, MqttFixtures.Parse("""{"choice":"permitJoin"}"""), Ct);
        form.StepId.ShouldBe(MqttSetupFlow.PermitJoinStep);

        var progress = (ProgressStep)await flow.ContinueAsync(MqttSetupFlow.PermitJoinStep, MqttFixtures.Parse("""{"seconds":120}"""), Ct);

        progress.StepId.ShouldBe(MqttSetupFlow.PermitJoinProgressStep);
        progress.Timeout.ShouldBe(TimeSpan.FromSeconds(120));
        broker.PublishedTo("z2m/bridge/request/permit_join").ShouldBe([new MqttPublish("z2m/bridge/request/permit_join", """{"time":120}""")]);

        await broker.PublishAsync("z2m/bridge/event", """{"type":"device_joined","data":{"friendly_name":"0x00158d00000000aa","ieee_address":"0x00158d00000000aa"}}""");
        time.Advance(TimeSpan.FromSeconds(30));
        var still = (ProgressStep)await flow.ContinueAsync(MqttSetupFlow.PermitJoinProgressStep, MqttFixtures.Parse("{}"), Ct);
        still.Timeout.ShouldBe(TimeSpan.FromSeconds(90));
        still.Description!.En.ShouldContain("0x00158d00000000aa");

        time.Advance(TimeSpan.FromSeconds(90));
        var done = (DoneStep)await flow.ContinueAsync(MqttSetupFlow.PermitJoinProgressStep, MqttFixtures.Parse("{}"), Ct);
        done.Config.ShouldBe(existing.Config);
        done.Secrets["password"].ShouldBe("stored-secret");
        broker.Clients.ShouldBeEmpty();
    }

    [Fact]
    public async Task Permit_join_can_be_stopped_early()
    {
        var broker = new FakeBroker();
        await using var flow = Factory(broker).CreateSetupFlow()!;
        var existing = Existing("""{"host":"umbrel.local"}""", null);
        await flow.StartAsync(Context(existing), Ct);
        await flow.ContinueAsync(MqttSetupFlow.PermitJoinStep, MqttFixtures.Parse("{}"), Ct);

        var done = await flow.ContinueAsync(MqttSetupFlow.PermitJoinProgressStep, MqttFixtures.Parse("""{"stop":true}"""), Ct);

        done.ShouldBeOfType<DoneStep>().Secrets.ShouldBeEmpty();
        broker.PublishedTo("zigbee2mqtt/bridge/request/permit_join").Select(p => p.Payload).ShouldBe(["""{"time":254}""", """{"time":0}"""]);
    }

    [Fact]
    public async Task Without_zigbee2mqtt_there_is_no_pairing_choice()
    {
        await using var flow = Factory(new FakeBroker()).CreateSetupFlow()!;

        var choose = (ChooseStep)await flow.StartAsync(Context(Existing("""{"host":"b","zigbee2mqttBaseTopic":""}""", null)), Ct);

        choose.Choices.Select(c => c.Key).ShouldBe([MqttSetupFlow.SettingsChoice]);
        (await flow.ContinueAsync(MqttSetupFlow.PermitJoinStep, MqttFixtures.Parse("{}"), Ct))
            .ShouldBeOfType<AbortStep>().Reason.ShouldBe("zigbee2mqtt_disabled");
        (await flow.ContinueAsync("nope", MqttFixtures.Parse("{}"), Ct)).ShouldBeOfType<AbortStep>().Reason.ShouldBe("unknown_step");
    }

    private static MqttConnectorFactory Factory(FakeBroker broker) => new(broker.Factory, MqttHarness.Options);

    private static FlowContext Context(IntegrationInstance? existing = null, TimeProvider? time = null) =>
        new(time ?? TimeProvider.System, NullLogger.Instance, existing);

    private static IntegrationInstance Existing(string config, string? password) =>
        new(Guid.NewGuid(), "My MQTT", MqttFixtures.Parse(config), new PasswordStore(password));
}

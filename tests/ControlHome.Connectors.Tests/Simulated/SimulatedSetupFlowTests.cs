using System.Text.Json;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.Simulated;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ControlHome.Connectors.Tests.Simulated;

/// <summary>The Simulated setup flow: a form, a "press the button" progress step, then done with a hub token secret.</summary>
public sealed class SimulatedSetupFlowTests
{
    private static readonly JsonElement _empty = SimulatedHarness.Json("{}");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Setup_is_a_form_then_press_the_button_then_done_with_a_token()
    {
        var time = new FakeTimeProvider(SimulatedHarness.Start);
        await using var flow = new SimulatedConnectorFactory().CreateSetupFlow()!;

        var form = (await flow.StartAsync(new FlowContext(time, NullLogger.Instance, null), Ct)).ShouldBeOfType<FormStep>();
        form.StepId.ShouldBe(SimulatedSetupFlow.SettingsStep);
        form.Title.En.ShouldBe("Choose what to simulate");
        form.Fields.Select(f => f.Key).ShouldBe(["preset", "language", "seed", "simulateReauth"]);
        form.Fields.Where(f => f.Advanced).Select(f => f.Key).ShouldBe(["language", "seed", "simulateReauth"], "test settings stay folded away");
        form.Values!.Value.GetRawText().ShouldBe("""{"preset":"basic","language":"en","seed":42,"simulateReauth":false}""");
        form.Errors.ShouldBeEmpty();

        var progress = (await flow.ContinueAsync(form.StepId, SimulatedHarness.Json("""{"preset":"demo","seed":7}"""), Ct)).ShouldBeOfType<ProgressStep>();
        progress.StepId.ShouldBe(SimulatedSetupFlow.PairingStep);
        progress.Timeout.ShouldBe(SimulatedSetupFlow.PairingTimeout);
        progress.Title.En.ShouldContain("Press the button");

        time.Advance(TimeSpan.FromSeconds(1));
        var waiting = (await flow.ContinueAsync(progress.StepId, _empty, Ct)).ShouldBeOfType<ProgressStep>();
        waiting.Timeout.ShouldBe(SimulatedSetupFlow.PairingTimeout - TimeSpan.FromSeconds(1));

        time.Advance(TimeSpan.FromSeconds(1));
        var done = (await flow.ContinueAsync(progress.StepId, _empty, Ct)).ShouldBeOfType<DoneStep>();
        done.Name.ShouldBe("Demo home");
        done.Config.GetRawText().ShouldBe("""{"preset":"demo","language":"en","seed":7,"simulateReauth":false}""");
        done.Secrets.Keys.ShouldBe([SimulatedSetupFlow.TokenSecret]);
        done.Secrets[SimulatedSetupFlow.TokenSecret].ShouldStartWith("sim-");
        done.Secrets[SimulatedSetupFlow.TokenSecret].Length.ShouldBe(36);
        JsonSerializer.Serialize<FlowStep>(done).ShouldNotContain("sim-", Case.Sensitive, "secrets are never serialized");
    }

    [Fact]
    public async Task A_portuguese_home_gets_portuguese_device_names_and_integration_name()
    {
        var time = new FakeTimeProvider(SimulatedHarness.Start);
        await using var flow = new SimulatedConnectorFactory().CreateSetupFlow()!;

        var form = (await flow.StartAsync(new FlowContext(time, NullLogger.Instance, null) { Language = "pt-BR" }, Ct))
            .ShouldBeOfType<FormStep>();
        form.Values!.Value.GetProperty("language").GetString().ShouldBe("pt-BR");

        await flow.ContinueAsync(form.StepId, form.Values.Value, Ct);
        time.Advance(SimulatedSetupFlow.PairingDelay);
        var done = (await flow.ContinueAsync(SimulatedSetupFlow.PairingStep, _empty, Ct)).ShouldBeOfType<DoneStep>();

        done.Name.ShouldBe("Casa simulada");
        done.Config.GetProperty("language").GetString().ShouldBe("pt-BR");
    }

    [Fact]
    public async Task An_invalid_form_comes_back_with_field_errors()
    {
        await using var flow = new SimulatedConnectorFactory().CreateSetupFlow()!;
        await flow.StartAsync(new FlowContext(TimeProvider.System, NullLogger.Instance, null), Ct);

        var again = (await flow.ContinueAsync(SimulatedSetupFlow.SettingsStep, SimulatedHarness.Json("""{"preset":"castle","colour":1}"""), Ct))
            .ShouldBeOfType<FormStep>();

        again.Errors.Keys.ShouldBe(["preset", "colour"], ignoreOrder: true);
        again.Errors["preset"].PtBr.ShouldBe("Este valor não é válido.");
        again.Values!.Value.GetProperty("preset").GetString().ShouldBe("castle");
    }

    [Fact]
    public async Task Pairing_times_out_and_unknown_steps_abort()
    {
        var time = new FakeTimeProvider(SimulatedHarness.Start);
        await using var flow = new SimulatedConnectorFactory().CreateSetupFlow()!;
        await flow.StartAsync(new FlowContext(time, NullLogger.Instance, null), Ct);

        (await flow.ContinueAsync(SimulatedSetupFlow.PairingStep, _empty, Ct)).ShouldBeOfType<AbortStep>().Reason.ShouldBe("not_started");
        await flow.ContinueAsync(SimulatedSetupFlow.SettingsStep, _empty, Ct);
        time.Advance(SimulatedSetupFlow.PairingTimeout);

        (await flow.ContinueAsync(SimulatedSetupFlow.PairingStep, _empty, Ct)).ShouldBeOfType<AbortStep>().Reason.ShouldBe("timeout");
        (await flow.ContinueAsync("nope", _empty, Ct)).ShouldBeOfType<AbortStep>().Reason.ShouldBe("unknown_step");
    }

    [Fact]
    public async Task Reauthorizing_starts_from_the_integration_settings_and_clears_the_expired_credentials()
    {
        var time = new FakeTimeProvider(SimulatedHarness.Start);
        var existing = new IntegrationInstance(
            Guid.NewGuid(),
            "My test home",
            SimulatedHarness.Json("""{"preset":"demo","seed":3,"simulateReauth":true}"""),
            EmptySecretStore.Instance);
        await using var flow = new SimulatedConnectorFactory().CreateSetupFlow()!;

        var form = (await flow.StartAsync(new FlowContext(time, NullLogger.Instance, existing) { Language = "pt-BR" }, Ct))
            .ShouldBeOfType<FormStep>();
        form.Values!.Value.GetRawText().ShouldBe(
            """{"preset":"demo","language":"en","seed":3,"simulateReauth":false}""",
            "its devices keep the language they were named in");

        await flow.ContinueAsync(form.StepId, form.Values.Value, Ct);
        time.Advance(SimulatedSetupFlow.PairingDelay);
        var done = (await flow.ContinueAsync(SimulatedSetupFlow.PairingStep, _empty, Ct)).ShouldBeOfType<DoneStep>();

        done.Name.ShouldBe("My test home");
        done.Config.GetProperty("simulateReauth").GetBoolean().ShouldBeFalse();
    }
}

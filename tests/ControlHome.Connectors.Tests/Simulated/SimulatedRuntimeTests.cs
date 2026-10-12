using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.Simulated;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;

namespace ControlHome.Connectors.Tests.Simulated;

public sealed class SimulatedRuntimeTests
{
    private const string Light = "light-1";
    private const string Plug = "plug-1";
    private const string Ac = "ac-1";
    private const string Contact = "contact-1";
    private const string DoorLock = "lock-1";

    [Fact]
    public async Task Emits_the_inventory_then_availability_and_initial_states()
    {
        await using var sim = await SimulatedHarness.StartAsync();
        var context = sim.Context;

        context.Devices.Select(d => d.ExternalId).ShouldBe([Light, Plug, Ac, Contact, DoorLock]);
        context.Devices.Select(d => d.DeviceClass).ShouldBe(
            [DeviceClasses.Light, DeviceClasses.Outlet, DeviceClasses.AirConditioner, DeviceClasses.Door, DeviceClasses.Lock]);
        Capabilities(context, Light).ShouldBe(["switch", "level"]);
        Capabilities(context, Plug).ShouldBe(["switch", "power", "energy"]);
        Capabilities(context, Ac).ShouldBe(["switch", "thermostat", "temperature"]);
        Capabilities(context, Contact).ShouldBe(["contact", "battery"]);
        Capabilities(context, DoorLock).ShouldBe(["lock"]);

        context.Availability.ShouldBe(context.Devices.Select(d => (d.ExternalId, Availability.Online)).ToList());
        context.Statuses.Select(s => s.Status).ShouldBe([IntegrationStatus.Connecting, IntegrationStatus.Ok]);

        var initial = context.States.Select(s => $"{s.ExternalDeviceId}/{s.CapabilityId}.{s.AttributeKey}").ToList();
        initial.ShouldBe(
        [
            "light-1/switch.on", "light-1/level.level",
            "plug-1/switch.on", "plug-1/power.power", "plug-1/energy.energy",
            "ac-1/switch.on", "ac-1/thermostat.mode", "ac-1/thermostat.targetTemperature", "ac-1/thermostat.currentTemperature",
            "ac-1/thermostat.fanMode", "ac-1/temperature.temperature",
            "contact-1/contact.open", "contact-1/battery.battery",
            "lock-1/lock.lockState",
        ]);
        context.States.ShouldAllBe(s => s.ObservedAtUtc == SimulatedHarness.Start && s.EndpointKey == "main");
        context.Latest(DoorLock, "lock", "lockState")!.Value.ShouldBe(StateValue.String("locked"));
    }

    [Fact]
    public async Task Every_report_fits_the_catalogue_and_the_device_constraints()
    {
        await using var sim = await SimulatedHarness.StartAsync();
        await sim.AdvanceTicksAsync(30);
        await sim.ExecuteAsync(Ac, "thermostat", "setMode", """{"mode":"heat"}""");
        await sim.ExecuteAsync(Light, "level", "setLevel", """{"level":5}""");

        var bindings = sim.Context.Devices.ToDictionary(
            d => d.ExternalId,
            d => d.Endpoints.Single().Capabilities.ToDictionary(c => c.CapabilityId));
        foreach (var report in sim.Context.States)
        {
            var binding = bindings[report.ExternalDeviceId].GetValueOrDefault(report.CapabilityId);
            binding.ShouldNotBeNull($"{report.ExternalDeviceId} has no {report.CapabilityId}");
            var attribute = CapabilityCatalog.Find(report.CapabilityId)!.FindAttribute(report.AttributeKey);
            attribute.ShouldNotBeNull($"{report.CapabilityId}.{report.AttributeKey}");
            var constraint = CapabilityConstraints.Parse(binding.Constraints).For(report.AttributeKey);
            ValueSchemaValidator.Accepts(attribute.Schema, report.Value, constraint)
                .ShouldBeTrue($"{report.ExternalDeviceId}/{report.CapabilityId}.{report.AttributeKey} = {report.Value}");
        }

        foreach (var device in sim.Context.Devices)
        {
            device.Connections.Keys.ShouldBe(["mac", "serial"], ignoreOrder: true);
            device.Manufacturer.ShouldBe("ControlHome");
            device.SuggestedRoom.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task Commands_apply_after_the_simulated_latency_and_complete()
    {
        await using var sim = await SimulatedHarness.StartAsync();
        var batch = new DeviceCommandBatch(Light, [new DeviceCommand("main", "switch", "turnOff", SimulatedHarness.Json("{}"))], "k-off");

        var pending = sim.Runtime.ExecuteAsync(batch, TestContext.Current.CancellationToken);
        sim.Time.Advance(SimulatedRuntime.CommandLatency - TimeSpan.FromMilliseconds(1));
        await Task.Delay(20, TestContext.Current.CancellationToken);
        pending.IsCompleted.ShouldBeFalse();
        sim.Context.Latest(Light, "switch", "on")!.Value.ShouldBe(StateValue.True);

        sim.Time.Advance(TimeSpan.FromMilliseconds(1));
        (await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ShouldBe(CommandOutcome.CompletedOutcome);
        var report = sim.Context.Latest(Light, "switch", "on")!;
        report.Value.ShouldBe(StateValue.False);
        report.ObservedAtUtc.ShouldBe(SimulatedHarness.Start + SimulatedRuntime.CommandLatency);
    }

    [Fact]
    public async Task Commands_change_state()
    {
        await using var sim = await SimulatedHarness.StartAsync();

        (await sim.ExecuteAsync(Light, "level", "setLevel", """{"level":40}""")).ShouldBeOfType<CommandOutcome.Completed>();
        sim.Context.Latest(Light, "level", "level")!.Value.ShouldBe(StateValue.Number(40));
        sim.Context.Latest(Light, "switch", "on")!.Value.ShouldBe(StateValue.True);

        await sim.ExecuteAsync(Light, "switch", "toggle");
        sim.Context.Latest(Light, "switch", "on")!.Value.ShouldBe(StateValue.False);

        await sim.ExecuteAsync(Ac, "thermostat", "setTargetTemperature", """{"targetTemperature":22.5}""");
        sim.Context.Latest(Ac, "thermostat", "targetTemperature")!.Value.ShouldBe(StateValue.Number(22.5));

        await sim.ExecuteAsync(Ac, "switch", "turnOff");
        sim.Context.Latest(Ac, "thermostat", "mode")!.Value.ShouldBe(StateValue.String("off"));
        await sim.ExecuteAsync(Ac, "thermostat", "setMode", """{"mode":"heat"}""");
        sim.Context.Latest(Ac, "switch", "on")!.Value.ShouldBe(StateValue.True);
        sim.Context.Latest(Ac, "thermostat", "mode")!.Value.ShouldBe(StateValue.String("heat"));

        await sim.ExecuteAsync(Ac, "thermostat", "setFanMode", """{"fanMode":"high"}""");
        sim.Context.Latest(Ac, "thermostat", "fanMode")!.Value.ShouldBe(StateValue.String("high"));

        await sim.ExecuteAsync(Plug, "switch", "turnOff");
        sim.Context.Latest(Plug, "power", "power")!.Value.ShouldBe(StateValue.Number(0));
    }

    [Theory]
    [InlineData(Light, "level", "setLevel", """{"level":0}""", CommandRejectionCodes.InvalidArguments)] // this light's minimum is 1
    [InlineData(Light, "level", "setLevel", """{}""", CommandRejectionCodes.InvalidArguments)]
    [InlineData(Ac, "thermostat", "setMode", """{"mode":"dry"}""", CommandRejectionCodes.InvalidArguments)] // not on this AC
    [InlineData(Ac, "thermostat", "setTargetTemperature", """{"targetTemperature":31}""", CommandRejectionCodes.InvalidArguments)]
    [InlineData(Light, "lock", "unlock", null, CommandRejectionCodes.UnsupportedCommand)]
    [InlineData(Light, "switch", "explode", null, CommandRejectionCodes.UnsupportedCommand)]
    [InlineData(Contact, "contact", "open", null, CommandRejectionCodes.UnsupportedCommand)]
    [InlineData("toaster-1", "switch", "turnOn", null, CommandRejectionCodes.UnknownDevice)]
    public async Task Invalid_commands_are_rejected_without_changing_state(
        string device,
        string capability,
        string command,
        string? arguments,
        string code)
    {
        await using var sim = await SimulatedHarness.StartAsync();
        var reportsBefore = sim.Context.States.Count;

        var outcome = await sim.ExecuteAsync(device, capability, command, arguments);

        outcome.ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe(code);
        sim.Context.States.Count.ShouldBe(reportsBefore);
    }

    [Fact]
    public async Task Mode_dependent_ranges_apply_to_the_current_mode()
    {
        await using var sim = await SimulatedHarness.StartAsync();

        (await sim.ExecuteAsync(Ac, "thermostat", "setTargetTemperature", """{"targetTemperature":28}""")).ShouldBe(CommandOutcome.CompletedOutcome);
        await sim.ExecuteAsync(Ac, "thermostat", "setMode", """{"mode":"heat"}""");

        // Heating tops out at 26 °C on this AC.
        (await sim.ExecuteAsync(Ac, "thermostat", "setTargetTemperature", """{"targetTemperature":28}"""))
            .ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe(CommandRejectionCodes.InvalidArguments);
    }

    [Fact]
    public async Task Unlock_is_at_most_once_per_idempotency_key()
    {
        await using var sim = await SimulatedHarness.StartAsync();

        (await sim.ExecuteAsync(DoorLock, "lock", "unlock", key: "unlock-1")).ShouldBe(CommandOutcome.CompletedOutcome);
        sim.Context.Latest(DoorLock, "lock", "lockState")!.Value.ShouldBe(StateValue.String("unlocked"));
        (await sim.ExecuteAsync(DoorLock, "lock", "lock", key: "lock-1")).ShouldBe(CommandOutcome.CompletedOutcome);
        sim.Context.Latest(DoorLock, "lock", "lockState")!.Value.ShouldBe(StateValue.String("locked"));

        // The same unlock again (a retry, a replay) is not executed.
        (await sim.ExecuteAsync(DoorLock, "lock", "unlock", key: "unlock-1"))
            .ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe(CommandRejectionCodes.Duplicate);
        sim.Context.Latest(DoorLock, "lock", "lockState")!.Value.ShouldBe(StateValue.String("locked"));

        // And an unlock must carry a key at all.
        (await sim.ExecuteAsync(DoorLock, "lock", "unlock", key: ""))
            .ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe(CommandRejectionCodes.IdempotencyRequired);
        sim.Context.Latest(DoorLock, "lock", "lockState")!.Value.ShouldBe(StateValue.String("locked"));
    }

    [Fact]
    public async Task Duplicate_keys_are_rejected_for_every_command_but_keys_expire()
    {
        await using var sim = await SimulatedHarness.StartAsync();

        (await sim.ExecuteAsync(Light, "switch", "turnOff", key: "same")).ShouldBe(CommandOutcome.CompletedOutcome);
        (await sim.ExecuteAsync(Light, "switch", "turnOn", key: "same")).ShouldBeOfType<CommandOutcome.Rejected>().Code
            .ShouldBe(CommandRejectionCodes.Duplicate);
        (await sim.ExecuteAsync(Light, "switch", "turnOn", key: "")).ShouldBe(CommandOutcome.CompletedOutcome); // not safety-critical

        sim.Time.Advance(SimulatedRuntime.IdempotencyWindow + TimeSpan.FromSeconds(1));
        (await sim.ExecuteAsync(Light, "switch", "turnOn", key: "same")).ShouldBe(CommandOutcome.CompletedOutcome);
    }

    [Fact]
    public async Task Plug_energy_integrates_power_over_simulated_time()
    {
        await using var sim = await SimulatedHarness.StartAsync();
        var plug = sim.Runtime.Home.Devices.OfType<SimulatedPlug>().Single();
        var startEnergy = plug.EnergyWh;

        await sim.AdvanceTicksAsync(24); // two minutes
        var powerWhileOn = PowerTimeline(sim.Context);
        powerWhileOn.Select(p => p.Value).Distinct().Count().ShouldBeGreaterThan(5, "power does a random walk while on");
        var afterTwoMinutes = SimulatedHarness.Start + (24 * SimulatedRuntime.TickInterval);
        plug.EnergyWh.ShouldBe(startEnergy + Integrate(powerWhileOn, SimulatedHarness.Start, afterTwoMinutes), tolerance: 1e-9);
        sim.Context.Latest(Plug, "energy", "energy")!.Value.N!.Value.ShouldBe(plug.EnergyWh, tolerance: 0.001);

        await sim.ExecuteAsync(Plug, "switch", "turnOff");
        var switchedOff = afterTwoMinutes + SimulatedRuntime.CommandLatency;
        var energyAtSwitchOff = plug.EnergyWh;
        energyAtSwitchOff.ShouldBe(startEnergy + Integrate(PowerTimeline(sim.Context), SimulatedHarness.Start, switchedOff), tolerance: 1e-9);

        await sim.AdvanceTicksAsync(12);
        sim.Context.Latest(Plug, "power", "power")!.Value.ShouldBe(StateValue.Number(0));
        plug.EnergyWh.ShouldBe(energyAtSwitchOff, "nothing is consumed while off");
    }

    [Fact]
    public async Task Ac_temperature_drifts_towards_the_target()
    {
        await using var sim = await SimulatedHarness.StartAsync();
        var initial = sim.Context.Latest(Ac, "thermostat", "currentTemperature")!.Value.N!.Value;
        initial.ShouldBeInRange(26, 30);

        await sim.AdvanceTicksAsync(10);

        var current = sim.Context.Latest(Ac, "thermostat", "currentTemperature")!.Value.N!.Value;
        current.ShouldBe(Math.Round(initial - 1.0, 2), tolerance: 1e-9); // 0.1 °C per tick towards 24 °C
        sim.Context.Latest(Ac, "temperature", "temperature")!.Value.N!.Value.ShouldBe(current);

        await sim.AdvanceTicksAsync(100);
        sim.Context.Latest(Ac, "thermostat", "currentTemperature")!.Value.ShouldBe(StateValue.Number(24));
    }

    [Fact]
    public async Task Same_seed_gives_the_same_first_twenty_reports()
    {
        var first = await FirstReportsAsync("""{"preset":"basic","seed":7}""", 20);
        var second = await FirstReportsAsync("""{"preset":"basic","seed":7}""", 20);

        second.ShouldBe(first);
        first.ShouldContain(r => r.CapabilityId == "power" && r.ObservedAtUtc > SimulatedHarness.Start, "random values after the first tick");
    }

    [Fact]
    public async Task A_different_seed_gives_different_reports()
    {
        var seven = await FirstReportsAsync("""{"seed":7}""", 20);
        var eight = await FirstReportsAsync("""{"seed":8}""", 20);

        eight.ShouldNotBe(seven);
    }

    [Fact]
    public async Task The_seed_defaults_to_the_options_then_42()
    {
        await using (var fromOptions = await SimulatedHarness.StartAsync("{}", new SimulatedConnectorOptions { Seed = 9 }))
        {
            fromOptions.Runtime.Settings.ShouldBe(new SimulatedSettings("basic", 9));
        }

        await using var defaults = await SimulatedHarness.StartAsync("{}");
        defaults.Runtime.Settings.ShouldBe(new SimulatedSettings("basic", 42));
        defaults.Context.Devices[0].Connections["serial"].ShouldBe("SIM-42-1");
    }

    [Fact]
    public async Task Refresh_reports_the_device_again()
    {
        await using var sim = await SimulatedHarness.StartAsync();
        var before = sim.Context.States.Count;

        await sim.Runtime.RefreshAsync(DoorLock, TestContext.Current.CancellationToken);
        await sim.Runtime.RefreshAsync("nope", TestContext.Current.CancellationToken);

        sim.Context.States.Skip(before).Select(s => $"{s.ExternalDeviceId}/{s.AttributeKey}").ShouldBe(["lock-1/lockState"]);
    }

    [Fact]
    public async Task Commands_are_rejected_while_not_running()
    {
        var factory = new SimulatedConnectorFactory();
        await using var runtime = factory.CreateRuntime(
            new IntegrationInstance(Guid.NewGuid(), "Sim", SimulatedHarness.Json("{}"), EmptySecretStore.Instance));

        var outcome = await runtime.ExecuteAsync(
            new DeviceCommandBatch(Light, [new DeviceCommand("main", "switch", "turnOn", SimulatedHarness.Json("{}"))], "k"),
            TestContext.Current.CancellationToken);

        outcome.ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe(CommandRejectionCodes.Unavailable);
    }

    [Fact]
    public async Task Stops_when_cancelled_and_can_run_again()
    {
        var sim = await SimulatedHarness.StartAsync();
        await sim.DisposeAsync();
        sim.Running.IsCompletedSuccessfully.ShouldBeTrue();

        var context = new RecordingContext(sim.Time);
        using var stop = new CancellationTokenSource();
        var again = sim.Runtime.RunAsync(context, stop.Token);
        context.States.Count.ShouldBe(14, "a restart reports every value again");
        await stop.CancelAsync();
        await again.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("""{"preset":"castle"}""")]
    [InlineData("""{"seed":-1}""")]
    [InlineData("""{"seed":1.5}""")]
    [InlineData("""{"colour":"red"}""")]
    [InlineData("[]")]
    public void Invalid_config_is_rejected(string config)
    {
        var factory = new SimulatedConnectorFactory();

        Should.Throw<ArgumentException>(() => factory.CreateRuntime(
            new IntegrationInstance(Guid.NewGuid(), "Sim", SimulatedHarness.Json(config), EmptySecretStore.Instance)));
    }

    [Fact]
    public void Describes_the_connector()
    {
        var descriptor = new SimulatedConnectorFactory().Descriptor;

        descriptor.Key.ShouldBe("simulated");
        descriptor.Category.ShouldBe(ConnectorCategory.Virtual);
        descriptor.ConfigFields.Select(f => f.Key).ShouldBe(["preset", "seed"]);
        descriptor.ConfigFields.ShouldAllBe(f => !f.Secret);
        descriptor.Features.PushInventory.ShouldBeTrue();
        new SimulatedConnectorFactory().CreateSetupFlow().ShouldBeNull();
    }

    private static List<string> Capabilities(RecordingContext context, string device) =>
        context.Devices.Single(d => d.ExternalId == device).Endpoints.Single().Capabilities.Select(c => c.CapabilityId).ToList();

    private static async Task<List<StateReport>> FirstReportsAsync(string config, int count)
    {
        await using var sim = await SimulatedHarness.StartAsync(config);
        while (sim.Context.States.Count < count)
        {
            await sim.AdvanceTicksAsync(1);
        }

        return sim.Context.States.Take(count).ToList();
    }

    private static List<(DateTimeOffset At, double Value)> PowerTimeline(RecordingContext context) =>
        context.States.Where(s => s.ExternalDeviceId == Plug && s.CapabilityId == "power")
            .Select(s => (s.ObservedAtUtc!.Value, s.Value.N!.Value))
            .ToList();

    /// <summary>∫ power dt in Wh, the power holding from each report until the next one.</summary>
    private static double Integrate(List<(DateTimeOffset At, double Value)> timeline, DateTimeOffset from, DateTimeOffset to)
    {
        var energy = 0d;
        for (var i = 0; i < timeline.Count; i++)
        {
            var start = timeline[i].At < from ? from : timeline[i].At;
            var end = i + 1 < timeline.Count ? timeline[i + 1].At : to;
            if (end > to)
            {
                end = to;
            }

            if (end > start)
            {
                energy += timeline[i].Value * (end - start).TotalHours;
            }
        }

        return energy;
    }
}

using System.Diagnostics;
using System.Text.Json.Nodes;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.Mqtt;
using ControlHome.Connectors.Mqtt.Transport;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using Microsoft.Extensions.Logging.Abstractions;

namespace ControlHome.Connectors.Tests.Mqtt;

/// <summary>
/// The MQTT connector over MQTTnet against a real Mosquitto 2.1.2 (password file, no anonymous access): discovery →
/// devices, state updates, command publishes, broker restart → reconnect + resubscribe, retained-empty config → orphaned.
/// Every test uses its own topics (and removes its retained messages), so they share one broker.
/// </summary>
[Collection(MosquittoCollection.Name)]
public sealed class MosquittoIntegrationTests(MosquittoFixture broker)
{
    private static readonly MqttRuntimeOptions _options = MqttRuntimeOptions.Default with
    {
        ReconnectInitial = TimeSpan.FromMilliseconds(250),
        ReconnectMax = TimeSpan.FromSeconds(2),
        ConnectionCheckInterval = TimeSpan.FromSeconds(1),
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Zigbee2mqtt_discovery_state_and_commands_flow_through_the_broker()
    {
        var baseTopic = "z2m-" + Unique();
        await using var observer = await broker.ClientAsync($"{baseTopic}/#");
        await observer.PublishAsync($"{baseTopic}/bridge/info", MqttFixtures.Text("z2m-bridge-info.json"), retain: true);
        await observer.PublishAsync($"{baseTopic}/bridge/devices", MqttFixtures.Text("z2m-bridge-devices.json"), retain: true);
        try
        {
            await using var run = await RunAsync($$"""{"zigbee2mqttBaseTopic":"{{baseTopic}}","discoveryPrefix":"","tasmotaDiscovery":false}""");
            await run.WaitUntilAsync(() => run.Context.Devices.Count == 14, "the bridge and 13 devices are discovered");
            run.Context.Statuses[^1].Status.ShouldBe(IntegrationStatus.Ok);

            await observer.PublishAsync($"{baseTopic}/kitchen_plug", """{"state":"ON","power":33.3,"energy":0.5}""");
            await run.WaitUntilAsync(() => Equals(run.Latest("0x00124b0000000002", CapabilityIds.Power, "power"), StateValue.Number(33.3)), "the power is reported");
            run.Latest("0x00124b0000000002", CapabilityIds.Energy, "energy").ShouldBe(StateValue.Number(500));
            run.Context.Availability.ShouldContain(("0x00124b0000000002", Availability.Online));

            var outcome = await run.ExecuteAsync("0x00124b0000000002", CapabilityIds.Switch, "turnOff");
            outcome.ShouldBe(CommandOutcome.AcceptedOutcome);
            (await observer.WaitForAsync($"{baseTopic}/kitchen_plug/set")).ShouldBe("""{"state":"OFF"}""");

            await run.ExecuteAsync("0x0017880100000001", CapabilityIds.Level, "setLevel", """{"level":100}""");
            (await observer.WaitForAsync($"{baseTopic}/living_room/ceiling/set", p => p.Contains("brightness", StringComparison.Ordinal)))
                .ShouldBe("""{"brightness":254}""");
        }
        finally
        {
            await observer.PublishAsync($"{baseTopic}/bridge/info", string.Empty, retain: true);
            await observer.PublishAsync($"{baseTopic}/bridge/devices", string.Empty, retain: true);
        }
    }

    [Fact]
    public async Task Home_assistant_discovery_and_a_retained_empty_config_orphans_the_device()
    {
        var prefix = "ha-" + Unique();
        var stateTopic = $"devices-{Unique()}/climate/state";
        var configTopic = $"{prefix}/sensor/climate/temperature/config";
        await using var observer = await broker.ClientAsync();
        await observer.PublishAsync(
            configTopic,
            $$$"""{"name":"Temperature","uniq_id":"climate-t","stat_t":"{{{stateTopic}}}","val_tpl":"{{ value_json.t | float }}","dev_cla":"temperature","unit_of_meas":"°C","dev":{"ids":"climate-1","name":"Climate"}}""",
            retain: true);
        try
        {
            await using var run = await RunAsync($$"""{"zigbee2mqttBaseTopic":"","discoveryPrefix":"{{prefix}}","tasmotaDiscovery":false}""");
            await run.WaitUntilAsync(() => run.Context.Devices.Any(d => d.ExternalId == "ha:climate-1"), "the device is discovered");
            await run.WaitUntilAsync(() => run.Runtime.Subscriptions.Contains(stateTopic), "its state topic is subscribed");

            await observer.PublishAsync(stateTopic, """{"t":"23.75"}""");
            await run.WaitUntilAsync(() => Equals(run.Latest("ha:climate-1", CapabilityIds.Temperature, "temperature"), StateValue.Number(23.75)), "the temperature is reported");

            await observer.PublishAsync(configTopic, string.Empty, retain: true);
            await run.WaitUntilAsync(() => run.Context.Removed.Contains("ha:climate-1"), "the device is orphaned");
        }
        finally
        {
            await observer.PublishAsync(configTopic, string.Empty, retain: true);
        }
    }

    [Fact]
    public async Task Tasmota_discovery_state_and_commands_flow_through_the_broker()
    {
        var mac = Convert.ToHexString(Guid.NewGuid().ToByteArray()[..6]);
        var topic = "tasmota_" + Unique();
        var config = JsonNode.Parse(MqttFixtures.Text("tasmota-plug-config.json"))!.AsObject();
        config["mac"] = mac;
        config["t"] = topic;
        await using var observer = await broker.ClientAsync($"cmnd/{topic}/#");
        await observer.PublishAsync($"tasmota/discovery/{mac}/config", config.ToJsonString(), retain: true);
        await observer.PublishAsync($"tasmota/discovery/{mac}/sensors", MqttFixtures.Text("tasmota-plug-sensors.json"), retain: true);
        await observer.PublishAsync($"tele/{topic}/LWT", "Online", retain: true);
        var id = "tasmota:" + mac;
        try
        {
            await using var run = await RunAsync("""{"zigbee2mqttBaseTopic":"","discoveryPrefix":"","tasmotaDiscovery":true}""");
            await run.WaitUntilAsync(() => run.Context.Devices.Any(d => d.ExternalId == id && d.DeviceClass == DeviceClasses.Outlet), "the plug is discovered");
            await run.WaitUntilAsync(() => run.Context.Availability.Contains((id, Availability.Online)), "the retained LWT says online");
            (await observer.WaitForAsync($"cmnd/{topic}/STATUS")).ShouldBe("10");

            await observer.PublishAsync($"tele/{topic}/SENSOR", """{"ENERGY":{"Power":120.5,"Total":3.25}}""");
            await run.WaitUntilAsync(() => Equals(run.Latest(id, CapabilityIds.Power, "power"), StateValue.Number(120.5)), "the power is reported");
            run.Latest(id, CapabilityIds.Energy, "energy").ShouldBe(StateValue.Number(3250));

            (await run.ExecuteAsync(id, CapabilityIds.Switch, "turnOn")).ShouldBe(CommandOutcome.AcceptedOutcome);
            (await observer.WaitForAsync($"cmnd/{topic}/POWER")).ShouldBe("ON");
        }
        finally
        {
            await observer.PublishAsync($"tasmota/discovery/{mac}/config", string.Empty, retain: true);
            await observer.PublishAsync($"tasmota/discovery/{mac}/sensors", string.Empty, retain: true);
            await observer.PublishAsync($"tele/{topic}/LWT", string.Empty, retain: true);
        }
    }

    [Fact]
    public async Task A_broker_restart_reconnects_and_resubscribes()
    {
        var prefix = "ha-" + Unique();
        var stateTopic = $"devices-{Unique()}/plug/state";
        var configTopic = $"{prefix}/sensor/plug/power/config";
        await using (var publisher = await broker.ClientAsync())
        {
            await publisher.PublishAsync(
                configTopic,
                $$$"""{"name":"Power","uniq_id":"plug-p","stat_t":"{{{stateTopic}}}","dev_cla":"power","unit_of_meas":"W","dev":{"ids":"plug-1"}}""",
                retain: true);
        }

        await using var run = await RunAsync($$"""{"zigbee2mqttBaseTopic":"","discoveryPrefix":"{{prefix}}","tasmotaDiscovery":false}""");
        await run.WaitUntilAsync(() => run.Runtime.Subscriptions.Contains(stateTopic), "the device's state topic is subscribed");

        await broker.RestartAsync(); // retained messages (the config) are gone with the restart

        await run.WaitUntilAsync(() => run.Context.Statuses.Any(s => s.Status == IntegrationStatus.Degraded), "the loss is reported");
        await run.WaitUntilAsync(
            () => run.Runtime.Connections >= 2 && run.Context.Statuses[^1].Status == IntegrationStatus.Ok,
            "the runtime reconnected and re-subscribed",
            TimeSpan.FromSeconds(30));
        run.Context.Availability.ShouldContain(("ha:plug-1", Availability.Unknown));
        run.Runtime.Subscriptions.ShouldContain(stateTopic);

        await using var observer = await broker.ClientAsync();
        await observer.PublishAsync(stateTopic, "42.5");
        await run.WaitUntilAsync(() => Equals(run.Latest("ha:plug-1", CapabilityIds.Power, "power"), StateValue.Number(42.5)), "a live message on the re-subscribed topic arrives");
        run.Context.Removed.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_discovery_config_with_a_malformed_topic_never_breaks_the_connection()
    {
        // Any client of the broker can publish discovery: a malformed or wildcard state topic in it must neither reach a
        // SUBSCRIBE (Mosquitto drops the connection; MQTTnet refuses it) nor poison the topics re-subscribed after a
        // reconnect.
        var prefix = "ha-" + Unique();
        var goodTopic = $"devices-{Unique()}/temperature";
        var badConfig = $"{prefix}/sensor/bad/temperature/config";
        var goodConfig = $"{prefix}/sensor/good/temperature/config";
        await using (var publisher = await broker.ClientAsync())
        {
            await publisher.PublishAsync(badConfig, """{"name":"Bad","uniq_id":"bad-t","stat_t":"bad/#/temperature","avty_t":"#","dev_cla":"temperature","dev":{"ids":"bad-1"}}""", retain: true);
            await publisher.PublishAsync(goodConfig, $$$"""{"name":"Good","uniq_id":"good-t","stat_t":"{{{goodTopic}}}","dev_cla":"temperature","unit_of_meas":"°C","dev":{"ids":"good-1"}}""", retain: true);
        }

        await using var run = await RunAsync($$"""{"zigbee2mqttBaseTopic":"","discoveryPrefix":"{{prefix}}","tasmotaDiscovery":false}""");
        await run.WaitUntilAsync(() => run.Context.Devices.Any(d => d.ExternalId == "ha:bad-1"), "the device with the bad topics is discovered");
        await run.WaitUntilAsync(() => run.Runtime.Subscriptions.Contains(goodTopic), "the good device's state topic is subscribed");
        run.Runtime.Subscriptions.ShouldBe([$"{prefix}/#", goodTopic], ignoreOrder: true);

        await broker.RestartAsync(); // forces a reconnect that re-subscribes every known device topic

        await run.WaitUntilAsync(
            () => run.Runtime.Connections >= 2 && run.Context.Statuses[^1].Status == IntegrationStatus.Ok,
            "the runtime reconnected and re-subscribed",
            TimeSpan.FromSeconds(30));
        await using var observer = await broker.ClientAsync();
        await observer.PublishAsync(goodTopic, "21.5");
        await run.WaitUntilAsync(() => Equals(run.Latest("ha:good-1", CapabilityIds.Temperature, "temperature"), StateValue.Number(21.5)), "the good sensor still reports");
        run.Context.Statuses[^1].Status.ShouldBe(IntegrationStatus.Ok);
    }

    [Fact]
    public async Task Wrong_credentials_need_reauth_and_the_setup_flow_reports_them_on_the_password()
    {
        var wrong = "wrong-" + Unique();
        var logger = new CapturingLogger();
        await using (var run = await RunAsync("""{"zigbee2mqttBaseTopic":"","discoveryPrefix":"","tasmotaDiscovery":false}""", wrong, logger, waitForConnection: false))
        {
            await run.WaitUntilAsync(() => run.Context.Statuses.Any(s => s.Status == IntegrationStatus.NeedsReauth), "the broker rejects the password");
            run.Context.Statuses.ShouldAllBe(s => s.Message == null || !s.Message.Contains(wrong));
            logger.Messages.ShouldAllBe(m => !m.Contains(wrong));
        }

        await using var flow = new MqttConnectorFactory(null, _options).CreateSetupFlow()!;
        await flow.StartAsync(new FlowContext(TimeProvider.System, NullLogger.Instance, null), Ct);
        var config = $$"""{"host":"{{broker.Host}}","port":{{broker.Port}},"username":"{{MosquittoFixture.User}}"}""";

        var rejected = await flow.ContinueAsync(MqttSetupFlow.ConnectionStep, MqttFixtures.Parse(config.Replace("}", $",\"password\":\"{wrong}\"}}", StringComparison.Ordinal)), Ct);
        rejected.ShouldBeOfType<FormStep>().Errors.Keys.ShouldBe(["password"]);

        var done = await flow.ContinueAsync(MqttSetupFlow.ConnectionStep, MqttFixtures.Parse(config.Replace("}", $",\"password\":\"{broker.Password}\"}}", StringComparison.Ordinal)), Ct);
        done.ShouldBeOfType<DoneStep>().Secrets["password"].ShouldBe(broker.Password);
        ((DoneStep)done).Config.GetRawText().ShouldNotContain(broker.Password);
    }

    private static string Unique() => Guid.NewGuid().ToString("N")[..10];

    private async Task<Run> RunAsync(string adapters, string? secret = null, CapturingLogger? logger = null, bool waitForConnection = true)
    {
        var config = JsonNode.Parse(adapters)!.AsObject();
        config["host"] = broker.Host;
        config["port"] = broker.Port;
        config["username"] = MosquittoFixture.User;
        var context = new RecordingContext(TimeProvider.System, logger);
        var runtime = (MqttRuntime)new MqttConnectorFactory(null, _options).CreateRuntime(
            new IntegrationInstance(context.IntegrationId, "MQTT", MqttFixtures.Parse(config.ToJsonString()), new PasswordStore(secret ?? broker.Password)));
        var run = new Run(runtime, context);
        if (waitForConnection)
        {
            await run.WaitUntilAsync(() => runtime.Connections > 0, "connected");
        }

        return run;
    }

    /// <summary>A running runtime with a real clock.</summary>
    private sealed class Run : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _running;

        public Run(MqttRuntime runtime, RecordingContext context)
        {
            Runtime = runtime;
            Context = context;
            _running = Task.Run(() => runtime.RunAsync(context, _stop.Token));
        }

        public MqttRuntime Runtime { get; }

        public RecordingContext Context { get; }

        public StateValue? Latest(string device, string capability, string attribute) =>
            Context.States.LastOrDefault(s => s.ExternalDeviceId == device && s.CapabilityId == capability && s.AttributeKey == attribute)?.Value;

        public Task<CommandOutcome> ExecuteAsync(string device, string capability, string command, string arguments = "{}") =>
            Runtime.ExecuteAsync(
                new DeviceCommandBatch(device, [new DeviceCommand("main", capability, command, MqttFixtures.Parse(arguments))], Guid.NewGuid().ToString("N")),
                Ct);

        public async Task WaitUntilAsync(Func<bool> condition, string because, TimeSpan? timeout = null)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (_running.IsCompleted)
                {
                    await _running;
                    throw new InvalidOperationException("The runtime stopped.");
                }

                if (watch.Elapsed > (timeout ?? TimeSpan.FromSeconds(15)))
                {
                    throw new TimeoutException($"Timed out waiting until {because}. Statuses: {string.Join(" | ", Context.Statuses)}");
                }

                await Task.Delay(20, Ct);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _running.WaitAsync(TimeSpan.FromSeconds(15));
            await Runtime.DisposeAsync();
            _stop.Dispose();
        }
    }
}

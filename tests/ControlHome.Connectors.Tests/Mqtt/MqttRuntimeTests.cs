using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.Mqtt;
using ControlHome.Connectors.Mqtt.Model;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;

namespace ControlHome.Connectors.Tests.Mqtt;

/// <summary>The runtime against an in-memory broker: discovery, state, commands, removal and the reconnect loop.</summary>
public sealed class MqttRuntimeTests
{
    private const string Plug = "0x00124b0000000002";
    private const string Bridge = "0x00124b0000c0ffee";

    [Fact]
    public async Task Zigbee2mqtt_inventory_creates_the_bridge_first_then_its_devices()
    {
        var broker = await BrokerWithZigbee2MqttAsync();
        await using var mqtt = await MqttHarness.StartAsync(broker, MqttHarness.Config(homeAssistant: false, tasmota: false));

        await mqtt.WaitUntilAsync(() => mqtt.Context.Devices.Count == 14, "the bridge and 13 devices are discovered");
        mqtt.Context.Devices[0].ExternalId.ShouldBe(Bridge);
        mqtt.Context.Devices.Skip(1).ShouldAllBe(d => d.ParentExternalId == Bridge);
        mqtt.Context.Statuses.Select(s => s.Status).ShouldBe([IntegrationStatus.Connecting, IntegrationStatus.Ok]);
        await mqtt.WaitForStateAsync(Bridge, "main", CapabilityIds.Generic, "version", StateValue.String("2.14.2"));
        mqtt.Runtime.Subscriptions.ShouldBe(["zigbee2mqtt/#"]);

        // Mains-powered devices with gettable properties are asked for their state once.
        broker.PublishedTo("zigbee2mqtt/kitchen_plug/get").ShouldHaveSingleItem().Payload.ShouldBe("""{"state":""}""");
        broker.PublishedTo("zigbee2mqtt/bedroom_climate/get").ShouldBeEmpty();
    }

    [Fact]
    public async Task State_messages_are_reported_and_imply_online()
    {
        var broker = await BrokerWithZigbee2MqttAsync();
        await using var mqtt = await MqttHarness.StartAsync(broker, MqttHarness.Config(homeAssistant: false, tasmota: false));
        await mqtt.WaitUntilAsync(() => mqtt.Context.Devices.Count == 14, "discovery");

        await broker.PublishAsync("zigbee2mqtt/kitchen_plug", """{"state":"ON","power":42.5,"energy":2.5}""");

        await mqtt.WaitForStateAsync(Plug, "main", CapabilityIds.Power, "power", StateValue.Number(42.5));
        mqtt.Latest(Plug, "main", CapabilityIds.Switch, "on")!.Value.ShouldBe(StateValue.True);
        mqtt.Latest(Plug, "main", CapabilityIds.Energy, "energy")!.Value.ShouldBe(StateValue.Number(2500));
        mqtt.Context.Availability.ShouldContain((Plug, Availability.Online));

        await broker.PublishAsync("zigbee2mqtt/kitchen_plug/availability", """{"state":"offline"}""");
        await mqtt.WaitUntilAsync(() => mqtt.Context.Availability[^1] == (Plug, Availability.Offline), "the plug is offline");
    }

    [Fact]
    public async Task Retained_state_received_before_the_inventory_is_replayed()
    {
        var broker = new FakeBroker();
        await broker.PublishAsync("zigbee2mqtt/kitchen_plug", """{"state":"OFF","power":0}""", retain: true);
        await broker.PublishAsync("zigbee2mqtt/bridge/devices", MqttFixtures.Text("z2m-bridge-devices.json"), retain: true);

        await using var mqtt = await MqttHarness.StartAsync(broker, MqttHarness.Config(homeAssistant: false, tasmota: false));

        await mqtt.WaitForStateAsync(Plug, "main", CapabilityIds.Switch, "on", StateValue.False);
    }

    [Fact]
    public async Task Commands_publish_to_the_device_and_are_accepted()
    {
        var broker = await BrokerWithZigbee2MqttAsync();
        await using var mqtt = await MqttHarness.StartAsync(broker, MqttHarness.Config(homeAssistant: false, tasmota: false));
        await mqtt.WaitUntilAsync(() => mqtt.Context.Devices.Count == 14, "discovery");

        var outcome = await mqtt.ExecuteAsync(Plug, CapabilityIds.Switch, "turnOff");

        outcome.ShouldBe(CommandOutcome.AcceptedOutcome);
        broker.PublishedTo("zigbee2mqtt/kitchen_plug/set").ShouldHaveSingleItem().Payload.ShouldBe("""{"state":"OFF"}""");

        var bridge = await mqtt.ExecuteAsync(Bridge, CapabilityIds.Generic, "permitJoin", """{"seconds":60}""");
        bridge.ShouldBe(CommandOutcome.AcceptedOutcome);
        broker.PublishedTo("zigbee2mqtt/bridge/request/permit_join").ShouldHaveSingleItem().Payload.ShouldBe("""{"time":60}""");
    }

    [Fact]
    public async Task Commands_are_at_most_once_and_validated()
    {
        var broker = await BrokerWithZigbee2MqttAsync();
        await using var mqtt = await MqttHarness.StartAsync(broker, MqttHarness.Config(homeAssistant: false, tasmota: false));
        await mqtt.WaitUntilAsync(() => mqtt.Context.Devices.Count == 14, "discovery");

        (await mqtt.ExecuteAsync(Plug, CapabilityIds.Switch, "turnOn", key: "k1")).ShouldBe(CommandOutcome.AcceptedOutcome);
        Code(await mqtt.ExecuteAsync(Plug, CapabilityIds.Switch, "turnOn", key: "k1")).ShouldBe(CommandRejectionCodes.Duplicate);
        broker.PublishedTo("zigbee2mqtt/kitchen_plug/set").Count.ShouldBe(1);

        Code(await mqtt.ExecuteAsync("0x00158d0000000007", CapabilityIds.Lock, "unlock", key: string.Empty)).ShouldBe(CommandRejectionCodes.IdempotencyRequired);
        Code(await mqtt.ExecuteAsync("nope", CapabilityIds.Switch, "turnOn")).ShouldBe(CommandRejectionCodes.UnknownDevice);
        Code(await mqtt.ExecuteAsync(Plug, CapabilityIds.Level, "setLevel", """{"level":5}""")).ShouldBe(CommandRejectionCodes.UnsupportedCommand);
        Code(await mqtt.ExecuteAsync("0x0017880100000001", CapabilityIds.Level, "setLevel", """{"level":500}""")).ShouldBe(CommandRejectionCodes.InvalidArguments);
    }

    [Fact]
    public async Task Devices_missing_from_a_newer_inventory_are_orphaned_and_renames_move_topics()
    {
        var broker = await BrokerWithZigbee2MqttAsync();
        await using var mqtt = await MqttHarness.StartAsync(broker, MqttHarness.Config(homeAssistant: false, tasmota: false));
        await mqtt.WaitUntilAsync(() => mqtt.Context.Devices.Count == 14, "discovery");

        await broker.PublishAsync("zigbee2mqtt/bridge/event", """{"type":"device_renamed","data":{"from":"kitchen_plug","to":"coffee_plug","homeassistant_rename":false}}""");
        await mqtt.WaitUntilAsync(() => mqtt.Context.Devices.Count(d => d.ExternalId == Plug) == 2, "the renamed plug is upserted again");
        await broker.PublishAsync("zigbee2mqtt/coffee_plug", """{"power":7}""");
        await mqtt.WaitForStateAsync(Plug, "main", CapabilityIds.Power, "power", StateValue.Number(7));
        (await mqtt.ExecuteAsync(Plug, CapabilityIds.Switch, "turnOn")).ShouldBe(CommandOutcome.AcceptedOutcome);
        broker.PublishedTo("zigbee2mqtt/coffee_plug/set").ShouldHaveSingleItem();

        var withoutPlug = MqttFixtures.Json("z2m-bridge-devices.json").EnumerateArray()
            .Where(e => e.GetProperty("ieee_address").GetString() != Plug)
            .Select(e => e.GetRawText());
        await broker.PublishAsync("zigbee2mqtt/bridge/devices", $"[{string.Join(',', withoutPlug)}]", retain: true);

        await mqtt.WaitUntilAsync(() => mqtt.Context.Removed.Contains(Plug), "the plug is orphaned");
        mqtt.Context.Removed.ShouldBe([Plug]);
        Code(await mqtt.ExecuteAsync(Plug, CapabilityIds.Switch, "turnOn")).ShouldBe(CommandRejectionCodes.UnknownDevice);
    }

    [Fact]
    public async Task Home_assistant_discovery_creates_devices_and_a_retained_empty_config_orphans_them()
    {
        var broker = new FakeBroker();
        foreach (var (topic, payload) in MqttFixtures.HaConfigs())
        {
            await broker.PublishAsync(topic, payload, retain: true);
        }

        await using var mqtt = await MqttHarness.StartAsync(broker, MqttHarness.Config(zigbee2Mqtt: true, tasmota: false));

        string[] expected = ["ha:ea334450945afc", "ha:01ad", "ha:office-lamp-1", "ha:esp-strip", "ha:porch-1", "ha:garage-1", "ha:front-lock-1", "ha:living-ac-1"];
        await mqtt.WaitUntilAsync(() => expected.All(id => mqtt.Context.Devices.Any(d => d.ExternalId == id)), "every Home Assistant device is discovered");
        mqtt.Context.Devices.ShouldNotContain(d => d.ExternalId.Contains("zigbee2mqtt", StringComparison.Ordinal)); // native adapter on: skipped
        broker.PublishedTo("homeassistant/status").ShouldHaveSingleItem().Payload.ShouldBe("online");

        // Dynamic state topics outside the prefix are subscribed per device.
        await mqtt.WaitUntilAsync(() => mqtt.Runtime.Subscriptions.Contains("kitchen/climate/state"), "the state topic is subscribed");
        mqtt.Runtime.Subscriptions.ShouldNotContain("homeassistant/binary_sensor/garden/state"); // covered by homeassistant/#
        await broker.PublishAsync("kitchen/climate/state", """{"temperature":22.25,"humidity":40}""");
        await mqtt.WaitForStateAsync("ha:ea334450945afc", "main", CapabilityIds.Temperature, "temperature", StateValue.Number(22.25));
        await broker.PublishAsync("kitchen/climate/availability", "online");
        await mqtt.WaitUntilAsync(() => mqtt.Context.Availability.Contains(("ha:ea334450945afc", Availability.Online)), "the device is online");
        mqtt.Latest("ha:ea334450945afc", "main", CapabilityIds.Generic, "unsupportedTemplates").ShouldNotBeNull();

        await broker.PublishAsync("homeassistant/lock/front/config", string.Empty, retain: true);
        await mqtt.WaitUntilAsync(() => mqtt.Context.Removed.Contains("ha:front-lock-1"), "the lock is orphaned");

        // Back again: upserted again.
        await broker.PublishAsync("homeassistant/lock/front/config", MqttFixtures.HaConfigs()["homeassistant/lock/front/config"], retain: true);
        await mqtt.WaitUntilAsync(() => mqtt.Context.Devices.Count(d => d.ExternalId == "ha:front-lock-1") == 2, "the lock is back");
    }

    [Fact]
    public async Task Removing_one_of_several_configs_keeps_the_device_with_the_rest()
    {
        var broker = new FakeBroker();
        var configs = MqttFixtures.HaConfigs();
        await broker.PublishAsync("homeassistant/light/esp-strip/strip/config", configs["homeassistant/light/esp-strip/strip/config"], retain: true);
        await broker.PublishAsync("homeassistant/button/esp-strip/restart/config", configs["homeassistant/button/esp-strip/restart/config"], retain: true);
        await using var mqtt = await MqttHarness.StartAsync(broker, MqttHarness.Config(zigbee2Mqtt: false, tasmota: false));
        await mqtt.WaitUntilAsync(() => mqtt.Context.Devices.Any(d => d.ExternalId == "ha:esp-strip" && d.Endpoints[0].Capabilities.Any(c => c.CapabilityId == "generic")), "strip with button");

        await broker.PublishAsync("homeassistant/button/esp-strip/restart/config", string.Empty, retain: true);

        await mqtt.WaitUntilAsync(() => mqtt.Device("ha:esp-strip").Endpoints[0].Capabilities.All(c => c.CapabilityId != "generic"), "the button is gone");
        mqtt.Context.Removed.ShouldBeEmpty();
    }

    [Fact]
    public async Task Tasmota_devices_are_discovered_commanded_and_removed()
    {
        var broker = new FakeBroker();
        await broker.PublishAsync("tasmota/discovery/DC4F22A1B2C3/config", MqttFixtures.Text("tasmota-plug-config.json"), retain: true);
        await broker.PublishAsync("tasmota/discovery/DC4F22A1B2C3/sensors", MqttFixtures.Text("tasmota-plug-sensors.json"), retain: true);
        await broker.PublishAsync("tele/tasmota_A1B2C3/LWT", "Online", retain: true);
        await using var mqtt = await MqttHarness.StartAsync(broker, MqttHarness.Config(zigbee2Mqtt: false, homeAssistant: false));

        await mqtt.WaitUntilAsync(() => mqtt.Context.Devices.Any(d => d.ExternalId == "tasmota:DC4F22A1B2C3" && d.DeviceClass == DeviceClasses.Outlet), "the plug with energy");
        await mqtt.WaitUntilAsync(() => mqtt.Runtime.Subscriptions.Contains("tele/tasmota_A1B2C3/SENSOR"), "the device topics are subscribed");
        await mqtt.WaitUntilAsync(() => mqtt.Context.Availability.Contains(("tasmota:DC4F22A1B2C3", Availability.Online)), "online from the retained LWT");
        broker.PublishedTo("cmnd/tasmota_A1B2C3/STATE").ShouldNotBeEmpty();

        await broker.PublishAsync("tele/tasmota_A1B2C3/SENSOR", """{"ENERGY":{"Power":99,"Total":1}}""");
        await mqtt.WaitForStateAsync("tasmota:DC4F22A1B2C3", "main", CapabilityIds.Power, "power", StateValue.Number(99));
        (await mqtt.ExecuteAsync("tasmota:DC4F22A1B2C3", CapabilityIds.Switch, "toggle")).ShouldBe(CommandOutcome.AcceptedOutcome);
        broker.PublishedTo("cmnd/tasmota_A1B2C3/POWER").ShouldHaveSingleItem().Payload.ShouldBe("TOGGLE");

        await broker.PublishAsync("tasmota/discovery/DC4F22A1B2C3/config", string.Empty, retain: true);
        await mqtt.WaitUntilAsync(() => mqtt.Context.Removed.Contains("tasmota:DC4F22A1B2C3"), "the plug is orphaned");
    }

    [Fact]
    public async Task A_lost_broker_marks_devices_unknown_then_reconnects_with_backoff_and_resubscribes()
    {
        var broker = new FakeBroker();
        var configs = MqttFixtures.HaConfigs();
        await broker.PublishAsync("homeassistant/device/kitchen-climate/config", configs["homeassistant/device/kitchen-climate/config"], retain: true);
        await using var mqtt = await MqttHarness.StartAsync(broker, MqttHarness.Config(zigbee2Mqtt: false, tasmota: false));
        await mqtt.WaitUntilAsync(() => mqtt.Context.Devices.Count == 1, "discovery");
        await broker.PublishAsync("kitchen/climate/availability", "online");
        await mqtt.WaitUntilAsync(() => mqtt.Context.Availability.Contains(("ha:ea334450945afc", Availability.Online)), "online");

        await broker.StopAsync();

        await mqtt.WaitUntilAsync(() => mqtt.Context.Statuses[^1].Status == IntegrationStatus.Degraded, "degraded");
        mqtt.Context.Statuses[^1].Message!.ShouldContain("broker.test:1883");
        mqtt.Context.Availability[^1].ShouldBe(("ha:ea334450945afc", Availability.Unknown));
        Code(await mqtt.ExecuteAsync("ha:ea334450945afc", CapabilityIds.Generic, "nothing")).ShouldBe(CommandRejectionCodes.UnsupportedCommand);
        var attempts = broker.Attempts.Count;
        var statuses = mqtt.Context.Statuses.Count;

        // 1 s, then 2 s, then 4 s between attempts while the broker stays down (each failure publishes a status once
        // the next timer is armed).
        mqtt.Time.Advance(TimeSpan.FromSeconds(1));
        await mqtt.WaitUntilAsync(() => mqtt.Context.Statuses.Count == statuses + 1, "the first retry failed");
        broker.Attempts.Count.ShouldBe(attempts + 1);
        mqtt.Context.Statuses[^1].Message!.ShouldStartWith("Cannot reach the MQTT broker at broker.test:1883");
        mqtt.Time.Advance(TimeSpan.FromSeconds(1.5));
        await mqtt.SettleAsync();
        broker.Attempts.Count.ShouldBe(attempts + 1);
        mqtt.Time.Advance(TimeSpan.FromSeconds(0.5));
        await mqtt.WaitUntilAsync(() => mqtt.Context.Statuses.Count == statuses + 2, "the second retry after 2 s failed");
        broker.Attempts.Count.ShouldBe(attempts + 2);

        broker.Start();
        mqtt.Time.Advance(TimeSpan.FromSeconds(4));
        await mqtt.WaitUntilAsync(() => mqtt.Runtime.Connections == 2 && mqtt.Context.Statuses[^1].Status == IntegrationStatus.Ok, "reconnected and subscribed");
        mqtt.Runtime.Subscriptions.ShouldBe(["homeassistant/#", "kitchen/climate/availability", "kitchen/climate/state"], ignoreOrder: true);
        broker.Clients.ShouldHaveSingleItem().Filters.ShouldContain("kitchen/climate/state");

        // The broker lost its retained messages; live messages on the re-subscribed topics still arrive.
        await broker.PublishAsync("kitchen/climate/state", """{"temperature":19}""");
        await mqtt.WaitForStateAsync("ha:ea334450945afc", "main", CapabilityIds.Temperature, "temperature", StateValue.Number(19));
        await broker.PublishAsync("kitchen/climate/availability", "online");
        await mqtt.WaitUntilAsync(() => mqtt.Context.Availability[^1] == ("ha:ea334450945afc", Availability.Online), "online again");
        mqtt.Context.Removed.ShouldBeEmpty(); // a lost broker never orphans devices
    }

    [Fact]
    public async Task Commands_are_refused_while_the_broker_is_unreachable()
    {
        var broker = await BrokerWithZigbee2MqttAsync();
        await using var mqtt = await MqttHarness.StartAsync(broker, MqttHarness.Config(homeAssistant: false, tasmota: false));
        await mqtt.WaitUntilAsync(() => mqtt.Context.Devices.Count == 14, "discovery");

        await broker.StopAsync();
        await mqtt.WaitUntilAsync(() => mqtt.Context.Statuses[^1].Status == IntegrationStatus.Degraded, "disconnected");

        Code(await mqtt.ExecuteAsync(Plug, CapabilityIds.Switch, "turnOn", key: "k")).ShouldBe(CommandRejectionCodes.Unavailable);

        // The key was not consumed: the same command succeeds once the broker is back.
        broker.Start();
        mqtt.Time.Advance(TimeSpan.FromSeconds(1));
        await mqtt.WaitUntilAsync(() => mqtt.Runtime.Connections == 2, "reconnected");
        (await mqtt.ExecuteAsync(Plug, CapabilityIds.Switch, "turnOn", key: "k")).ShouldBe(CommandOutcome.AcceptedOutcome);
    }

    [Fact]
    public async Task Rejected_credentials_need_reauth_and_the_password_never_leaks()
    {
        var logger = new CapturingLogger();
        var broker = new FakeBroker { Credentials = ("controlhome", "the-right-one") };
        const string WrongPassword = "CANARY-wrong-password-6f1e";

        await using var mqtt = await MqttHarness.StartAsync(broker, MqttHarness.Config(), WrongPassword, logger, waitForConnection: false);

        await mqtt.WaitUntilAsync(() => mqtt.Context.Statuses.Any(s => s.Status == IntegrationStatus.NeedsReauth), "needs reauth");
        broker.Attempts[0].Password.ShouldBe(WrongPassword);
        broker.Attempts[0].ClientId.ShouldBe($"controlhome-{mqtt.Context.IntegrationId:D}");
        mqtt.Context.Statuses.ShouldAllBe(s => s.Message == null || !s.Message.Contains(WrongPassword));
        logger.Messages.ShouldNotBeEmpty();
        logger.Messages.ShouldAllBe(m => !m.Contains(WrongPassword));
        broker.Attempts[0].ToString().ShouldNotContain(WrongPassword);

        // It keeps trying (the broker's users may be fixed without touching ControlHome).
        mqtt.Time.Advance(TimeSpan.FromSeconds(1));
        await mqtt.WaitUntilAsync(() => broker.Attempts.Count == 2, "a retry");
    }

    [Fact]
    public async Task A_device_the_host_cannot_save_fails_the_run_and_the_restart_rediscovers_it()
    {
        var broker = await BrokerWithZigbee2MqttAsync();
        var first = new RecordingContext(TimeProvider.System);
        var runtime = (MqttRuntime)new MqttConnectorFactory(broker.Factory, MqttHarness.Options).CreateRuntime(new IntegrationInstance(
            first.IntegrationId,
            "MQTT",
            MqttFixtures.Parse(MqttHarness.Config(homeAssistant: false, tasmota: false)),
            new PasswordStore("p")));

        // Losing the device silently would hide it until Zigbee2MQTT publishes its inventory again: the run fails instead.
        var failure = await Should.ThrowAsync<MqttInventoryException>(
            () => runtime.RunAsync(new FlakyInventoryContext(first, failures: 1), Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct));
        failure.InnerException.ShouldBeOfType<InvalidOperationException>();
        broker.Clients.ShouldBeEmpty();

        // The supervisor runs it again: the broker replays the retained inventory.
        var second = new RecordingContext(TimeProvider.System);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var running = runtime.RunAsync(second, stop.Token);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (second.Devices.Count < 14 && watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(10, Ct);
        }

        second.Devices.Count.ShouldBe(14);
        second.Devices[0].ExternalId.ShouldBe(Bridge);
        await stop.CancelAsync();
        await running;
    }

    [Fact]
    public async Task Every_report_of_the_fixture_devices_fits_their_capabilities()
    {
        var broker = await BrokerWithZigbee2MqttAsync();
        foreach (var (topic, payload) in MqttFixtures.HaConfigs())
        {
            await broker.PublishAsync(topic, payload, retain: true);
        }

        await broker.PublishAsync("tasmota/discovery/C44F33D4E5F6/config", MqttFixtures.Text("tasmota-th-config.json"), retain: true);
        await broker.PublishAsync("tasmota/discovery/C44F33D4E5F6/sensors", MqttFixtures.Text("tasmota-th-sensors.json"), retain: true);
        await using var mqtt = await MqttHarness.StartAsync(broker);
        await mqtt.WaitUntilAsync(() => mqtt.Context.Devices.Select(d => d.ExternalId).Distinct().Count() == 14 + 8 + 1, "everything discovered");

        foreach (var (topic, payload) in new[]
        {
            ("zigbee2mqtt/living_room/ceiling", """{"state":"ON","brightness":200,"color_temp":300,"color":{"x":0.2,"y":0.6}}"""),
            ("zigbee2mqtt/bedroom_trv", """{"system_mode":"auto","occupied_heating_setpoint":19,"local_temperature":18.2}"""),
            ("zigbee2mqtt/desk_remote", """{"action":"double","battery":100}"""),
            ("home/office/lamp/hs", "359.9,100"),
            ("esp-strip/light/strip/state", """{"state":"OFF","brightness":0}"""),
            ("garage/door/position", "77"),
            ("living/ac/mode", "cool"),
            ("garden_th/tele/SENSOR", """{"AM2301":{"Temperature":30,"Humidity":99.9,"DewPoint":29},"DS18B20":{"Temperature":-2}}"""),
        })
        {
            await broker.PublishAsync(topic, payload);
        }

        await mqtt.SettleAsync();
        mqtt.Context.States.Count.ShouldBeGreaterThan(20);
        foreach (var group in mqtt.Context.States.GroupBy(s => s.ExternalDeviceId))
        {
            mqtt.Device(group.Key).ShouldAllFit(group.Select(s => (MqttReading)new MqttReading.State(s.EndpointKey, s.CapabilityId, s.AttributeKey, s.Value)));
        }

        mqtt.Context.Events.ShouldContain(e => e.ExternalDeviceId == "0x00158d000000000a" && e.CapabilityId == "button" && e.Event == "double");
    }

    private static async Task<FakeBroker> BrokerWithZigbee2MqttAsync()
    {
        var broker = new FakeBroker();
        await broker.PublishAsync("zigbee2mqtt/bridge/info", MqttFixtures.Text("z2m-bridge-info.json"), retain: true);
        await broker.PublishAsync("zigbee2mqtt/bridge/devices", MqttFixtures.Text("z2m-bridge-devices.json"), retain: true);
        return broker;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Code(CommandOutcome outcome) => outcome.ShouldBeOfType<CommandOutcome.Rejected>().Code;
}

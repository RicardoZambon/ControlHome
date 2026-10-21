using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.LgThinQ;
using ControlHome.Connectors.LgThinQ.Mqtt;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace ControlHome.Connectors.Tests.LgThinQ;

/// <summary>
/// The runtime against WireMock (REST) and an in-memory broker: discovery, the first poll, subscriptions, the MQTT client
/// certificate, partial reports, notifications, commands, reconnects, the 5-minute polling rule, token and quota errors and
/// the nightly energy run.
/// </summary>
public sealed class LgRuntimeTests : IDisposable
{
    private const string Ac = "test-ac-0001";
    private const string Tower = "test-tower-0004";
    private const string Fridge = "test-fridge-0003";

    private readonly FakeLgCloud _cloud = new();

    public void Dispose() => _cloud.Dispose();

    private FakeLgCloud Account() => _cloud.Account("broker.test", 8883, (Ac, "ac"), (Tower, "washtower"), (Fridge, "refrigerator"));

    [Fact]
    public async Task A_session_discovers_polls_subscribes_and_connects_with_a_new_client_certificate()
    {
        Account();
        await using var lg = await LgHarness.StartAsync(_cloud);

        lg.Context.Syncs.ShouldHaveSingleItem().ShouldBe([Ac, Fridge, Tower]);
        await lg.WaitForStateAsync(Ac, CapabilityIds.Thermostat, "targetTemperature", StateValue.Number(19));
        lg.Latest(Tower, CapabilityIds.ApplianceCycle, "machineState", "washer")!.Value.ShouldBe(StateValue.String("running"));
        lg.Latest(Fridge, CapabilityIds.Generic, "targetTemperature", "freezer")!.Value.ShouldBe(StateValue.Number(-18));
        lg.Context.Availability.ShouldContain((Ac, Availability.Online));

        foreach (var device in new[] { Ac, Tower, Fridge })
        {
            _cloud.Requests("GET", $"/devices/{device}/state").Count.ShouldBe(1);
            _cloud.Requests("POST", $"/push/{device}/subscribe").Count.ShouldBe(1);
            _cloud.Requests("POST", $"/event/{device}/subscribe").Count.ShouldBe(1);
        }

        _cloud.Requests("POST", "/push/devices").Count.ShouldBe(1);
        _cloud.Requests("POST", "/client").Count.ShouldBe(1);
        _cloud.Certificates.ShouldBe(1);
        var stored = LgMqttCredentials.FromJson(lg.Secrets[LgSecrets.MqttCredentials]).ShouldNotBeNull();
        stored.Topic.ShouldBe(FakeLgCloud.Topic);
        stored.PrivateKeyPem.ShouldStartWith("-----BEGIN PRIVATE KEY-----");

        var connection = lg.Broker.Connections.ShouldHaveSingleItem();
        (connection.Host, connection.Port, connection.ClientId).ShouldBe(("broker.test", 8883, LgHarness.ClientId));
        connection.ClientCertificate.HasPrivateKey.ShouldBeTrue();
        lg.Broker.Subscriptions.ShouldBe([FakeLgCloud.Topic]);
        await lg.WaitForStatusAsync(IntegrationStatus.Ok);
        _cloud.Requests().ShouldAllBe(r => FakeLgCloud.Header(r, "x-client-id") == LgHarness.ClientId);
    }

    [Fact]
    public async Task Partial_reports_merge_into_the_last_status_and_notifications_become_events()
    {
        Account();
        await using var lg = await LgHarness.StartAsync(_cloud);
        await lg.WaitForStateAsync(Ac, CapabilityIds.Thermostat, "mode", StateValue.String("cool"));

        await lg.PublishAsync("""{"deviceId":"test-ac-0001","deviceType":"DEVICE_AIR_CONDITIONER","pushType":"DEVICE_STATUS","report":{"temperatureInUnits":[{"unit":"C","targetTemperature":22}]}}""");
        await lg.WaitForStateAsync(Ac, CapabilityIds.Thermostat, "targetTemperature", StateValue.Number(22));
        lg.Latest(Ac, CapabilityIds.Thermostat, "mode")!.Value.ShouldBe(StateValue.String("cool")); // merged, not replaced

        await lg.PublishAsync("""{"deviceId":"test-tower-0004","deviceType":"DEVICE_WASHTOWER","pushType":"DEVICE_STATUS","report":{"dryer":{"runState":{"currentState":"RUNNING"}}}}""");
        await lg.WaitForStateAsync(Tower, CapabilityIds.ApplianceCycle, "machineState", StateValue.String("running"), "dryer");

        await lg.PublishAsync("""{"deviceId":"test-tower-0004","deviceType":"DEVICE_WASHTOWER","pushType":"DEVICE_PUSH","pushCode":"DRYING_IS_COMPLETE"}""");
        await lg.WaitUntilAsync(() => lg.Context.Events.Count == 1, "the notification is reported");
        var notification = lg.Context.Events[0];
        (notification.ExternalDeviceId, notification.EndpointKey, notification.AttributeKey, notification.Event)
            .ShouldBe((Tower, "dryer", "notification", "drying_is_complete"));

        // Junk and messages for unknown appliances are skipped; the next good message still arrives.
        await lg.PublishAsync("not json");
        await lg.PublishAsync("""{"deviceId":"test-gone-9999","pushType":"DEVICE_STATUS","report":{}}""");
        await lg.PublishAsync("""{"deviceId":"test-fridge-0003","pushType":"DEVICE_STATUS","report":{"doorStatus":[{"locationName":"MAIN","doorState":"OPEN"}]}}""");
        await lg.WaitForStateAsync(Fridge, CapabilityIds.Contact, "open", StateValue.True);
    }

    [Fact]
    public async Task Commands_become_conditional_control_calls_at_most_once()
    {
        Account();
        _cloud.Fails("POST", $"/devices/{Tower}/control", "2301");
        await using var lg = await LgHarness.StartAsync(_cloud);

        var accepted = await lg.ExecuteAsync(Ac, CapabilityIds.Thermostat, "setTargetTemperature", """{"targetTemperature":24}""", key: "key-1");
        var duplicate = await lg.ExecuteAsync(Ac, CapabilityIds.Thermostat, "setTargetTemperature", """{"targetTemperature":24}""", key: "key-1");
        var refused = await lg.ExecuteAsync(Tower, CapabilityIds.ApplianceCycle, "start", endpoint: "washer");
        var unknown = await lg.ExecuteAsync("test-gone-9999", CapabilityIds.Switch, "turnOn");

        accepted.ShouldBe(CommandOutcome.AcceptedOutcome);
        duplicate.ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe(CommandRejectionCodes.Duplicate);
        var rejection = refused.ShouldBeOfType<CommandOutcome.Rejected>();
        (rejection.Code, rejection.Message.Contains("Remote Start", StringComparison.Ordinal)).ShouldBe((CommandRejectionCodes.Refused, true));
        unknown.ShouldBeOfType<CommandOutcome.Rejected>().Code.ShouldBe(CommandRejectionCodes.UnknownDevice);

        var control = _cloud.Requests("POST", $"/devices/{Ac}/control").ShouldHaveSingleItem();
        control.Body.ShouldBe("""{"temperatureInUnits":{"coolTargetTemperature":24,"unit":"C"}}""");
        FakeLgCloud.Header(control, "x-conditional-control").ShouldBe("true");
        _cloud.Requests("POST", $"/devices/{Tower}/control").ShouldHaveSingleItem().Body.ShouldBe("""{"washer":{"operation":{"washerOperationMode":"START"}}}""");
    }

    [Fact]
    public async Task A_reconnect_resubscribes_and_polls_only_devices_not_polled_for_five_minutes()
    {
        Account();
        await using var lg = await LgHarness.StartAsync(_cloud);
        await lg.WaitForStateAsync(Ac, CapabilityIds.Switch, "on", StateValue.True);

        // A drop right away: reconnect after the back-off, no new subscriptions or polls (too recent).
        await lg.Broker.DropAsync();
        await lg.WaitForStatusAsync(IntegrationStatus.Degraded);
        lg.Time.Advance(TimeSpan.FromSeconds(1));
        await lg.WaitUntilAsync(() => lg.Runtime.Connections == 2, "the runtime reconnected");
        await lg.WaitForStatusAsync(IntegrationStatus.Ok);
        lg.Broker.Subscriptions.ShouldBe([FakeLgCloud.Topic, FakeLgCloud.Topic]);
        _cloud.Requests("POST", $"/event/{Ac}/subscribe").Count.ShouldBe(1);
        _cloud.Requests("GET", $"/devices/{Ac}/state").Count.ShouldBe(1);

        // Ten minutes later: event subscriptions are posted again and every device is re-read.
        lg.Time.Advance(TimeSpan.FromMinutes(10));
        await lg.Broker.DropAsync();
        await lg.WaitForStatusAsync(IntegrationStatus.Degraded);
        lg.Time.Advance(TimeSpan.FromSeconds(2));
        await lg.WaitUntilAsync(() => lg.Runtime.Connections == 3, "the runtime reconnected again");
        await lg.WaitUntilAsync(() => _cloud.Requests("GET", $"/devices/{Fridge}/state").Count == 2, "stale devices are re-read");
        _cloud.Requests("POST", $"/event/{Ac}/subscribe").Count.ShouldBe(2);
        _cloud.Requests("POST", $"/push/{Ac}/subscribe").Count.ShouldBe(1);
        _cloud.Certificates.ShouldBe(1); // the stored certificate is reused
        _cloud.Requests("GET", "/route").Count.ShouldBe(1);
    }

    [Fact]
    public async Task While_mqtt_is_down_each_device_is_polled_every_five_minutes()
    {
        Account();
        var broker = new FakeLgBroker();
        for (var i = 0; i < 50; i++)
        {
            broker.Failures.Enqueue(LgMqttFailure.Unreachable);
        }

        await using var lg = await LgHarness.StartAsync(_cloud, broker, waitForMqtt: false);
        await lg.WaitUntilAsync(() => broker.Connections.Count >= 1, "a connection was attempted");
        await lg.WaitForStatusAsync(IntegrationStatus.Degraded);
        _cloud.Requests("GET", $"/devices/{Ac}/state").Count.ShouldBe(1);

        lg.Time.Advance(TimeSpan.FromMinutes(4));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        _cloud.Requests("GET", $"/devices/{Ac}/state").Count.ShouldBe(1);

        lg.Time.Advance(TimeSpan.FromMinutes(1));
        await lg.WaitUntilAsync(() => _cloud.Requests("GET", $"/devices/{Ac}/state").Count == 2, "the device is polled after five minutes");
        lg.Context.Statuses.ShouldContain(s => s.Status == IntegrationStatus.Degraded && s.Message!.Contains("polling", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_rejected_certificate_is_replaced_at_most_once_a_minute()
    {
        Account();
        var broker = new FakeLgBroker();
        broker.Failures.Enqueue(LgMqttFailure.TlsRejected);
        broker.Failures.Enqueue(LgMqttFailure.NotAuthorized);
        await using var lg = await LgHarness.StartAsync(_cloud, broker, waitForMqtt: false);
        await lg.WaitUntilAsync(() => Refusals(lg) == 1, "the first attempt is refused");
        _cloud.Certificates.ShouldBe(1);
        var first = lg.Secrets[LgSecrets.MqttCredentials];

        lg.Time.Advance(TimeSpan.FromSeconds(1));
        await lg.WaitUntilAsync(() => Refusals(lg) == 2, "the second attempt is refused");
        _cloud.Certificates.ShouldBe(1); // one rejection could be a glitch

        // Two rejections in a row: a new certificate, but only once a minute has passed since the last request.
        DateTimeOffset? issuedAt = null;
        await lg.WaitUntilAsync(
            () =>
            {
                lg.Time.Advance(TimeSpan.FromSeconds(5));
                issuedAt ??= _cloud.Certificates == 2 ? lg.Time.GetUtcNow() : null;
                return lg.Runtime.MqttConnected;
            },
            "the runtime connects with a new certificate");
        _cloud.Certificates.ShouldBe(2);
        issuedAt!.Value.ShouldBeGreaterThanOrEqualTo(LgHarness.Start.AddMinutes(1));
        lg.Secrets[LgSecrets.MqttCredentials].ShouldNotBe(first);
    }

    [Fact]
    public async Task A_refused_certificate_request_keeps_the_session_and_is_retried()
    {
        _cloud.Server.Given(Request.Create().WithPath("/client/certificate").UsingPost()).AtPriority(1).InScenario("certificate").WillSetStateTo("issued")
            .RespondWith(Response.Create().WithStatusCode(400).WithBody(LgFixtures.Error("1312")));
        Account();
        await using var lg = await LgHarness.StartAsync(_cloud, waitForMqtt: false);

        await lg.WaitUntilAsync(
            () => lg.Context.Statuses.Any(s => s.Message?.Contains("refused to set up the real-time client", StringComparison.Ordinal) == true),
            "the refusal is reported");
        lg.Running.IsCompleted.ShouldBeFalse();

        await lg.WaitUntilAsync(
            () =>
            {
                lg.Time.Advance(TimeSpan.FromSeconds(5));
                return lg.Runtime.MqttConnected;
            },
            "the runtime connects once LG issues the certificate");
        _cloud.Requests("POST", "/client/certificate").Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_restart_reuses_the_certificate_and_skips_polls_younger_than_five_minutes()
    {
        Account();
        var broker = new FakeLgBroker();
        using var factory = new LgThinQConnectorFactory(null, LgHarness.Options(_cloud), broker.Factory);
        var secrets = new MemorySecretStore((LgSecrets.Pat, LgHarness.Pat), (LgSecrets.ClientId, LgHarness.ClientId));
        await using (var first = await LgHarness.StartAsync(_cloud, broker, factory, secrets))
        {
            await first.WaitForStateAsync(Ac, CapabilityIds.Switch, "on", StateValue.True);
        }

        await using var second = await LgHarness.StartAsync(_cloud, broker, factory, secrets);

        await second.WaitForStateAsync(Ac, CapabilityIds.Switch, "on", StateValue.True); // the last status, reported again
        _cloud.Certificates.ShouldBe(1);
        _cloud.Requests("GET", $"/devices/{Ac}/state").Count.ShouldBe(1);
        _cloud.Requests("GET", $"/devices/{Ac}/profile").Count.ShouldBe(1); // profiles are cached
        _cloud.Requests("POST", "/client").Count.ShouldBe(2); // registration is idempotent and done every start
    }

    [Fact]
    public async Task A_rejected_token_needs_reauth_and_the_runtime_stops_calling()
    {
        _cloud.Fails("GET", "/devices", "1103", 401);
        await using var lg = await LgHarness.StartAsync(_cloud, waitForMqtt: false);

        await lg.WaitForStatusAsync(IntegrationStatus.NeedsReauth);
        lg.Context.Statuses[^1].Message!.ShouldContain("connect-pat.lgthinq.com");
        lg.Time.Advance(TimeSpan.FromDays(1));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        _cloud.Requests().Count.ShouldBe(1);
        lg.Context.Syncs.ShouldBeEmpty(); // nothing is orphaned
    }

    [Fact]
    public async Task The_call_limit_pauses_the_integration_for_an_hour()
    {
        _cloud.Server.Given(Request.Create().WithPath("/devices").UsingGet()).InScenario("quota").WillSetStateTo("ok")
            .RespondWith(Response.Create().WithStatusCode(429).WithBody(LgFixtures.Error("1306")));
        Account();
        _cloud.Server.Given(Request.Create().WithPath("/devices").UsingGet()).AtPriority(1).InScenario("quota").WhenStateIs("ok")
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(LgFixtures.Ok(
                """[{"deviceId":"test-ac-0001","deviceInfo":{"deviceType":"DEVICE_AIR_CONDITIONER","modelName":"PAC_910604_WW","alias":"Living room AC","reportable":true}}]""")));
        await using var lg = await LgHarness.StartAsync(_cloud, waitForMqtt: false);

        await lg.WaitForStatusAsync(IntegrationStatus.QuotaExceeded);
        lg.Time.Advance(TimeSpan.FromMinutes(59));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        _cloud.Requests("GET", "/devices").Count.ShouldBe(1);

        lg.Time.Advance(TimeSpan.FromMinutes(2));
        await lg.WaitUntilAsync(() => _cloud.Requests("GET", "/devices").Count == 2, "the device list is asked again after an hour");
    }

    [Fact]
    public async Task The_nightly_run_reports_days_and_months_as_energy_windows()
    {
        Account();
        _cloud.Server.Given(Request.Create().WithPath($"/devices/energy/{Ac}/usage").WithParam("period", "DAILY").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(LgFixtures.Ok(
                """{"resultCode":"0000","result":{"dataList":[{"usedDate":"20261020","energyUsage":1200},{"usedDate":"20261021","energyUsage":300}]}}""")));
        _cloud.Server.Given(Request.Create().WithPath($"/devices/energy/{Ac}/usage").WithParam("period", "MONTHLY").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(LgFixtures.Ok(
                """{"resultCode":"0000","result":{"dataList":[{"usedDate":"202609","energyUsage":30000},{"usedDate":"202610","energyUsage":21000}]}}""")));
        _cloud.Fails("GET", $"/devices/energy/{Tower}/usage", "1219");
        _cloud.Fails("GET", $"/devices/energy/{Fridge}/usage", "1219");
        await using var lg = await LgHarness.StartAsync(_cloud);
        await lg.WaitUntilAsync(() => lg.Runtime.NextEnergyRun is not null, "the energy run is scheduled");

        lg.Runtime.NextEnergyRun.ShouldBe(new DateTimeOffset(2026, 10, 22, 6, 0, 0, TimeSpan.Zero)); // 03:00 in São Paulo
        lg.Time.SetUtcNow(lg.Runtime.NextEnergyRun!.Value);
        await lg.WaitUntilAsync(() => lg.Runtime.EnergyRuns == 1, "the energy run finished");

        var daily = _cloud.Requests("GET", $"/devices/energy/{Ac}/usage").First(r => r.Query!["period"].Single() == "DAILY");
        (daily.Query!["property"].Single(), daily.Query["startDate"].Single(), daily.Query["endDate"].Single()).ShouldBe(("energyUsage", "20260901", "20261021"));
        var monthly = _cloud.Requests("GET", $"/devices/energy/{Ac}/usage").First(r => r.Query!["period"].Single() == "MONTHLY");
        (monthly.Query!["startDate"].Single(), monthly.Query["endDate"].Single()).ShouldBe(("202609", "202610"));

        var windows = lg.Context.EnergyReports.ShouldHaveSingleItem();
        windows.ShouldBe(
        [
            new EnergyWindowReport(Ac, "main", Utc(10, 20), Utc(10, 21), 1200),
            new EnergyWindowReport(Ac, "main", Utc(10, 21), Utc(10, 22), 300),
            new EnergyWindowReport(Ac, "main", Utc(9, 1), Utc(10, 1), 30000),
            new EnergyWindowReport(Ac, "main", Utc(10, 1), Utc(10, 22), 21000),
        ]);
    }

    [Fact]
    public async Task With_energy_off_no_energy_is_asked_and_the_session_keeps_running()
    {
        Account();
        await using var lg = await LgHarness.StartAsync(_cloud, config: """{"country":"BR","energy":false}""");

        lg.Time.Advance(TimeSpan.FromDays(2));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        lg.Running.IsCompleted.ShouldBeFalse();
        lg.Runtime.MqttConnected.ShouldBeTrue();
        _cloud.Requests().ShouldNotContain(r => r.Path.Contains("/energy/", StringComparison.Ordinal));
        lg.Runtime.Devices.ShouldAllBe(d => d.Energy.Count == 0);
    }

    [Fact]
    public async Task A_device_list_change_triggers_a_rediscovery()
    {
        Account();
        await using var lg = await LgHarness.StartAsync(_cloud);

        await lg.PublishAsync("""{"pushType":"DEVICE_ALIAS_CHANGED","deviceId":"test-ac-0001","alias":"Quarto"}""");

        await lg.WaitUntilAsync(
            () =>
            {
                lg.Time.Advance(TimeSpan.FromSeconds(1));
                return lg.Context.Syncs.Count == 2;
            },
            "the inventory is read again after the quiet time");
        (lg.Time.GetUtcNow() - LgHarness.Start).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(30));
    }

    private static int Refusals(LgHarness lg) =>
        lg.Context.Statuses.Count(s => s.Status == IntegrationStatus.Degraded && s.Message!.Contains("Cannot connect", StringComparison.Ordinal));

    private static DateTimeOffset Utc(int month, int day) => new(2026, month, day, 3, 0, 0, TimeSpan.Zero);
}

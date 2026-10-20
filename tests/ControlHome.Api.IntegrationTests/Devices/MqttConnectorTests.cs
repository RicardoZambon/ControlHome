using System.Net.Http.Json;
using ControlHome.Application.Integrations;
using ControlHome.Domain.Capabilities;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Api.IntegrationTests.Devices;

/// <summary>
/// The MQTT connector as the app registers it (<c>AddConnector&lt;MqttConnectorFactory&gt;()</c>): listed in the catalogue
/// with a write-only password, and the password (a <c>CANARY-…</c> value) only ever lives in the encrypted secrets — not
/// in the stored config, API responses, hub payloads, status messages or logs — while the runtime keeps failing to reach
/// a broker. Broker behaviour itself is covered against Mosquitto in <c>ControlHome.Connectors.Tests</c>.
/// </summary>
[Collection(DevicesCollection.Name)]
public sealed class MqttConnectorTests(DevicesApiFactory factory) : DevicesTestBase(factory)
{
    [Fact]
    public async Task The_mqtt_connector_is_listed_with_a_write_only_password()
    {
        var (_, _, viewer) = await SignInAllAsync();

        var mqtt = (await viewer.GetFromJsonAsync<ConnectorDto[]>("/api/connectors", Ct))!.Single(c => c.Key == "mqtt");

        mqtt.Category.ShouldBe("protocol");
        mqtt.Features.ShouldBe(new ConnectorFeaturesDto(SetupFlow: true, Reauth: false, PushInventory: true, PermitJoin: true, Cloud: false, Polling: false));
        mqtt.ConfigFields.Select(f => f.Key).ShouldBe(
            ["host", "port", "useTls", "username", "password", "clientId", "discoveryPrefix", "zigbee2mqttBaseTopic", "tasmotaDiscovery"]);
        mqtt.ConfigFields.Where(f => f.Secret).Select(f => f.Key).ShouldBe(["password"]);
        mqtt.ConfigFields.Single(f => f.Key == "port").Schema.ShouldBeOfType<NumberSchema>().Max.ShouldBe(65535);
    }

    [Fact]
    public async Task The_broker_password_never_leaves_the_secret_store()
    {
        var canary = $"CANARY-{Guid.NewGuid():N}";
        var (admin, _, _) = await SignInAllAsync();

        // Nothing listens on port 1: the runtime reads the password from the secret store, fails, backs off and retries.
        var integration = await CreateIntegrationAsync(
            admin,
            "mqtt",
            $$"""{"host":"127.0.0.1","port":1,"username":"controlhome","password":"{{canary}}","tasmotaDiscovery":false}""");

        await RecordingRealtimePublisher.WaitUntilAsync(
            () => Factory.Realtime.IntegrationStatuses.Any(s => s.Id == integration.Id && s.Status == "Degraded"),
            "the MQTT integration reports that the broker is unreachable");
        var status = Factory.Realtime.IntegrationStatuses.Last(s => s.Id == integration.Id && s.Status == "Degraded");
        status.Message.ShouldNotBeNull().ShouldContain("127.0.0.1:1");

        var bodies = new List<string>();
        foreach (var path in new[] { "/api/integrations", "/api/connectors", "/api/devices" })
        {
            bodies.Add(await admin.GetStringAsync(path, Ct));
        }

        var stored = await QueryDbAsync(db => db.Integrations.AsNoTracking().SingleAsync(i => i.Id == integration.Id, Ct));
        var audit = await QueryDbAsync(db => db.AuditLog.AsNoTracking().Select(e => e.DetailsJson ?? string.Empty).ToListAsync(Ct));

        stored.ConfigJson.ShouldContain("127.0.0.1");
        stored.ConfigJson.ShouldNotContain("password");
        stored.ConfigJson.ShouldNotContain(canary);
        stored.SecretsProtected.ShouldNotBeNull();
        stored.SecretsProtected.ShouldNotContain(canary);
        (stored.StatusMessage ?? string.Empty).ShouldNotContain(canary);
        bodies.ShouldNotContain(b => b.Contains(canary, StringComparison.Ordinal));
        Factory.Realtime.AllPayloadsJson().ShouldNotContain(canary);
        audit.ShouldNotContain(d => d.Contains(canary, StringComparison.Ordinal));
        Factory.Logs.Events.ShouldContain(e => e.Contains("127.0.0.1:1", StringComparison.Ordinal)); // the failures were logged…
        Factory.Logs.Events.ShouldNotContain(e => e.Contains(canary, StringComparison.Ordinal)); // …without the password
    }
}

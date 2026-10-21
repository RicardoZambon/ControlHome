using System.Net.Http.Json;
using ControlHome.Application.Integrations;
using ControlHome.Domain.Capabilities;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Api.IntegrationTests.Devices;

/// <summary>
/// The Intelbras / Dahua HTTP connector as the app registers it: listed with a write-only password (the XPE relay kind
/// hidden while its flag is off), and the device password (a <c>CANARY-…</c> value) only ever lives in the encrypted
/// secrets while the runtime keeps failing to reach the device. Device behaviour is covered against WireMock in
/// <c>ControlHome.Connectors.Tests</c>.
/// </summary>
[Collection(DevicesCollection.Name)]
public sealed class DahuaHttpConnectorTests(DevicesApiFactory factory) : DevicesTestBase(factory)
{
    [Fact]
    public async Task The_connector_is_listed_with_a_write_only_password_and_no_xpe_relay()
    {
        var (_, _, viewer) = await SignInAllAsync();

        var dahua = (await viewer.GetFromJsonAsync<ConnectorDto[]>("/api/connectors", Ct))!.Single(c => c.Key == "dahuahttp");

        dahua.Category.ShouldBe("vendor");
        dahua.DisplayName.PtBr.ShouldBe("Câmeras, gravadores e controle de acesso Intelbras");
        dahua.Features.ShouldBe(new ConnectorFeaturesDto(SetupFlow: true, Reauth: true, PushInventory: true, PermitJoin: false, Cloud: false, Polling: false));
        dahua.ConfigFields.Select(f => f.Key).ShouldBe(["host", "port", "username", "password", "kind", "channels", "rtspPort", "doorUserId", "language"]);
        dahua.ConfigFields.Where(f => f.Secret).Select(f => f.Key).ShouldBe(["password"]);
        dahua.ConfigFields.Single(f => f.Key == "kind").Schema.ShouldBeOfType<EnumSchema>().Values.ShouldBe(["auto", "camera", "recorder", "accessControl"]);
    }

    [Fact]
    public async Task The_device_password_never_leaves_the_secret_store()
    {
        var canary = $"CANARY-{Guid.NewGuid():N}";
        var (admin, _, _) = await SignInAllAsync();

        // Nothing listens on port 1: the runtime reads the password, fails to reach the device, backs off and retries.
        var integration = await CreateIntegrationAsync(
            admin,
            "dahuahttp",
            $$"""{"host":"127.0.0.1","port":1,"username":"controlhome","password":"{{canary}}","kind":"camera"}""");

        await RecordingRealtimePublisher.WaitUntilAsync(
            () => Factory.Realtime.IntegrationStatuses.Any(s => s.Id == integration.Id && s.Status == "Degraded"),
            "the integration reports that the device is unreachable");
        Factory.Realtime.IntegrationStatuses.Last(s => s.Id == integration.Id && s.Status == "Degraded").Message.ShouldNotBeNull().ShouldContain("127.0.0.1:1");

        var bodies = new List<string>();
        foreach (var path in new[] { "/api/integrations", "/api/connectors", "/api/devices" })
        {
            bodies.Add(await admin.GetStringAsync(path, Ct));
        }

        var stored = await QueryDbAsync(db => db.Integrations.AsNoTracking().SingleAsync(i => i.Id == integration.Id, Ct));
        stored.ConfigJson.ShouldNotContain("password");
        stored.ConfigJson.ShouldNotContain(canary);
        stored.SecretsProtected.ShouldNotBeNull().ShouldNotContain(canary);
        (stored.StatusMessage ?? string.Empty).ShouldNotContain(canary);
        bodies.ShouldNotContain(b => b.Contains(canary, StringComparison.Ordinal));
        Factory.Realtime.AllPayloadsJson().ShouldNotContain(canary);
        Factory.Logs.Events.ShouldContain(e => e.Contains("127.0.0.1:1", StringComparison.Ordinal));
        Factory.Logs.Events.ShouldNotContain(e => e.Contains(canary, StringComparison.Ordinal));
    }
}

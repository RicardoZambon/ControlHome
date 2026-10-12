using System.Net.Http.Json;
using ControlHome.Application.Realtime;
using ControlHome.Domain.Capabilities;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Api.IntegrationTests.Devices;

/// <summary>
/// A secret (<c>CANARY-&lt;guid&gt;</c>) given to a connector that leaks it everywhere it can — logs, exception messages,
/// status messages, command rejections — never leaves the process: not in logs, API responses, hub payloads or the
/// database's plain columns.
/// </summary>
[Collection(DevicesCollection.Name)]
public sealed class CanaryTests(DevicesApiFactory factory) : DevicesTestBase(factory)
{
    [Fact]
    public async Task A_secret_never_appears_in_logs_responses_or_hub_payloads()
    {
        var canary = $"CANARY-{Guid.NewGuid():N}";
        var (admin, member, _) = await SignInAllAsync();
        var bodies = new List<string>();

        var integration = await CreateIntegrationAsync(admin, CanaryConnectorFactory.Key, $$"""{"token":"{{canary}}","label":"kitchen"}""");

        // The first run fails (with the token in the exception), the restart works and imports one device.
        var devices = await WaitForDevicesAsync(member, 1);
        await RecordingRealtimePublisher.WaitUntilAsync(
            () => Factory.Realtime.IntegrationStatuses.Any(s => s.Id == integration.Id && s.Status == "Degraded"),
            "the canary reports Degraded");
        var commandId = await SendAcceptedAsync(member, MainEndpoint(devices[0]).Id, CapabilityIds.Switch, "turnOn");
        (await Factory.Realtime.WaitForCommandAsync(commandId, CommandStatuses.Failed)).Code.ShouldBe("commands.refused");

        foreach (var path in new[] { "/api/integrations", "/api/devices", "/api/state", "/api/connectors", $"/api/devices/{devices[0].Id}" })
        {
            bodies.Add(await admin.GetStringAsync(path, Ct));
        }

        await RecordingRealtimePublisher.WaitUntilAsync(async () => (await AuditEntriesAsync("commands.executed")).Count == 1, "audited");
        var audit = await QueryDbAsync(db => db.AuditLog.AsNoTracking().Select(e => e.DetailsJson ?? string.Empty).ToListAsync(Ct));
        var stored = await QueryDbAsync(db => db.Integrations.AsNoTracking().SingleAsync(i => i.Id == integration.Id, Ct));

        // Proof that the leaks happened and were masked.
        var logs = Factory.Logs.Events;
        logs.ShouldContain(e => e.Contains("Canary connecting with token \"***\"", StringComparison.Ordinal));
        logs.ShouldContain(e => e.Contains("access_token=***", StringComparison.Ordinal));
        logs.ShouldContain(e => e.Contains("Login failed for token ***", StringComparison.Ordinal));
        Factory.Realtime.IntegrationStatuses.ShouldContain(s => s.Message == "Using token ***");

        logs.ShouldNotContain(e => e.Contains(canary, StringComparison.Ordinal));
        bodies.ShouldNotContain(b => b.Contains(canary, StringComparison.Ordinal));
        Factory.Realtime.AllPayloadsJson().ShouldNotContain(canary);
        audit.ShouldNotContain(d => d.Contains(canary, StringComparison.Ordinal));
        stored.ConfigJson.ShouldNotContain(canary);
        stored.ConfigJson.ShouldContain("kitchen");
        stored.SecretsProtected.ShouldNotBeNull();
        stored.SecretsProtected.ShouldNotContain(canary);
        (stored.StatusMessage ?? string.Empty).ShouldNotContain(canary);
    }

    [Fact]
    public async Task Secret_config_fields_are_required_and_write_only()
    {
        var (admin, _, _) = await SignInAllAsync();

        using var missing = await admin.PostAsJsonAsync(
            "/api/integrations",
            new { connectorKey = CanaryConnectorFactory.Key, name = "No token", config = new { label = "x" } },
            Ct);

        await missing.ShouldBeProblemAsync(System.Net.HttpStatusCode.BadRequest, "integrations.invalid_config");
        var json = await missing.Content.ReadAsStringAsync(Ct);
        json.ShouldContain("\"config.token\":[\"commands.argument_required\"]");
    }
}

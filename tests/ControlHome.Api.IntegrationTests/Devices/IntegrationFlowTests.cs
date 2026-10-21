using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ControlHome.Application.Devices;
using ControlHome.Application.Integrations;
using ControlHome.Domain.Devices;
using ControlHome.Domain.Security;
using ControlHome.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Api.IntegrationTests.Devices;

/// <summary>Setup and reauth flows: step by step, in memory, owned by the admin who started them; done saves the integration.</summary>
[Collection(DevicesCollection.Name)]
public sealed class IntegrationFlowTests(DevicesApiFactory factory) : DevicesTestBase(factory)
{
    [Fact]
    public async Task The_simulated_setup_flow_creates_the_integration_with_its_token_encrypted()
    {
        var (admin, _, viewer) = await SignInAllAsync();

        var form = await StartAsync(admin, "simulated");
        (form.Type, form.StepId).ShouldBe(("form", "settings"));
        form.Fields.Select(f => f.Key).ShouldBe(["preset", "seed", "simulateReauth"]);
        form.Values!.Value.GetProperty("preset").GetString().ShouldBe("basic");
        form.Title!.PtBr.ShouldBe("Casa simulada");

        var progress = await ContinueAsync(admin, form.FlowId, """{"preset":"basic","seed":5}""");
        (progress.Type, progress.StepId, progress.TimeoutSeconds).ShouldBe(("progress", "pairing", 60d));
        (await ContinueAsync(admin, form.FlowId, "{}")).Type.ShouldBe("progress", "the button presses itself after 2 s");

        await AdvanceClockAsync(admin, 3);
        var done = await ContinueAsync(admin, form.FlowId, "{}");

        done.Type.ShouldBe("done");
        var integrationId = done.IntegrationId.ShouldNotBeNull();
        (await viewer.GetFromJsonAsync<IntegrationDto[]>("/api/integrations", Ct))!.ShouldHaveSingleItem().Name.ShouldBe("Simulated home");
        await WaitForDevicesAsync(viewer, 5);
        var saved = await QueryDbAsync(db => db.Integrations.AsNoTracking().SingleAsync(i => i.Id == integrationId, Ct));
        saved.ConfigJson.ShouldNotContain("hubToken");
        var secrets = Factory.Services.GetRequiredService<IIntegrationSecretProtector>().Read(saved.SecretsProtected);
        var token = secrets.Values["hubToken"];
        token.ShouldStartWith("sim-");
        saved.SecretsProtected.ShouldNotBeNull().ShouldNotContain(token);
        var audit = (await AuditEntriesAsync("integrations.created")).ShouldHaveSingleItem();
        audit.DetailsJson.ShouldNotBeNull().ShouldContain("hubToken");
        audit.DetailsJson.ShouldNotContain(token);

        using var again = await ContinueRawAsync(admin, form.FlowId, "{}");
        await again.ShouldBeProblemAsync(HttpStatusCode.NotFound, "flows.not_found");
    }

    [Fact]
    public async Task A_connector_without_its_own_flow_gets_one_form_and_secret_fields_stay_write_only()
    {
        var (admin, _, _) = await SignInAllAsync();
        var token = $"CANARY-{Guid.NewGuid():N}";

        var form = await StartAsync(admin, CanaryConnectorFactory.Key);
        form.Fields.Select(f => (f.Key, f.Secret)).ShouldBe([("token", true), ("label", false)]);
        var missing = await ContinueAsync(admin, form.FlowId, """{"label":"x"}""");
        (missing.Type, missing.Errors.Keys.Single()).ShouldBe(("form", "token"));

        using var response = await ContinueRawAsync(admin, form.FlowId, $$"""{"token":"{{token}}","label":"porch"}""");
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain(token);
        var done = JsonSerializer.Deserialize<FlowStepDto>(body, _jsonOptions)!;

        var saved = await QueryDbAsync(db => db.Integrations.AsNoTracking().SingleAsync(i => i.Id == done.IntegrationId, Ct));
        saved.ConfigJson.ShouldBe("""{"label":"porch"}""");
        Factory.Services.GetRequiredService<IIntegrationSecretProtector>().Read(saved.SecretsProtected).Values["token"].ShouldBe(token);

        // Reconfiguring without typing the token again keeps it; the form never shows it.
        var reauth = await StartReauthAsync(admin, saved.Id);
        reauth.Values!.Value.GetRawText().ShouldBe("""{"label":"porch"}""");
        var updated = await ContinueAsync(admin, reauth.FlowId, """{"label":"garden"}""");
        updated.IntegrationId.ShouldBe(saved.Id);
        var after = await QueryDbAsync(db => db.Integrations.AsNoTracking().SingleAsync(i => i.Id == saved.Id, Ct));
        after.ConfigJson.ShouldBe("""{"label":"garden"}""");
        Factory.Services.GetRequiredService<IIntegrationSecretProtector>().Read(after.SecretsProtected).Values["token"].ShouldBe(token);
        Factory.Logs.Events.ShouldNotContain(e => e.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_empty_secret_field_counts_as_left_empty()
    {
        var (admin, _, _) = await SignInAllAsync();
        var token = $"CANARY-{Guid.NewGuid():N}";
        var form = await StartAsync(admin, CanaryConnectorFactory.Key);

        // A password box sent empty is no token: required, so the form asks again.
        var empty = await ContinueAsync(admin, form.FlowId, """{"token":"","label":"x"}""");
        (empty.Type, empty.Errors.Keys.Single()).ShouldBe(("form", "token"));
        var done = await ContinueAsync(admin, form.FlowId, $$"""{"token":"{{token}}","label":"porch"}""");
        var integrationId = done.IntegrationId.ShouldNotBeNull();

        // Reconfiguring with the password box left empty keeps the stored token.
        var reauth = await StartReauthAsync(admin, integrationId);
        (await ContinueAsync(admin, reauth.FlowId, """{"token":"","label":"garden"}""")).Type.ShouldBe("done");
        var after = await QueryDbAsync(db => db.Integrations.AsNoTracking().SingleAsync(i => i.Id == integrationId, Ct));
        after.ConfigJson.ShouldBe("""{"label":"garden"}""");
        Factory.Services.GetRequiredService<IIntegrationSecretProtector>().Read(after.SecretsProtected).Values["token"].ShouldBe(token);
    }

    [Fact]
    public async Task Reauthorizing_clears_expired_credentials_and_keeps_the_devices()
    {
        var (admin, _, viewer) = await SignInAllAsync();
        var integration = await CreateIntegrationAsync(admin, config: """{"preset":"basic","seed":3}""");
        var devices = await WaitForDevicesAsync(viewer, 5);
        var light = DeviceOfClass(devices, DeviceClasses.Light);
        using (var rename = await admin.PutAsJsonAsync($"/api/devices/{light.Id}", new UpdateDeviceRequest("Lustre", null, null), Ct))
        {
            await rename.ShouldHaveStatusAsync(HttpStatusCode.OK);
        }

        // The hub "rejects its token": the reauth flow with expired credentials ticked drives NeedsReauth.
        var broken = await StartReauthAsync(admin, integration.Id);
        await ContinueAsync(admin, broken.FlowId, """{"preset":"basic","seed":3,"simulateReauth":true}""");
        await AdvanceClockAsync(admin, 3);
        (await ContinueAsync(admin, broken.FlowId, "{}")).Type.ShouldBe("done");
        await WaitForStatusAsync(viewer, integration.Id, "NeedsReauth");
        (await viewer.GetFromJsonAsync<IntegrationDto[]>("/api/integrations", Ct))!.Single().StatusMessage
            .ShouldBe("The simulated hub rejected the stored token. Run the reauthorization flow.");

        var reauth = await StartReauthAsync(admin, integration.Id);
        reauth.Values!.Value.GetProperty("simulateReauth").GetBoolean().ShouldBeFalse();
        await ContinueAsync(admin, reauth.FlowId, reauth.Values.Value.GetRawText());
        await AdvanceClockAsync(admin, 3);
        var done = await ContinueAsync(admin, reauth.FlowId, "{}");

        done.IntegrationId.ShouldBe(integration.Id);
        await WaitForStatusAsync(viewer, integration.Id, "Ok");
        var after = await WaitForDevicesAsync(viewer, 5);
        after.Single(d => d.Id == light.Id).Name.ShouldBe("Lustre");
        (await AuditEntriesAsync("integrations.updated")).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Devices_missing_from_a_full_inventory_become_orphaned_not_deleted()
    {
        var (admin, _, viewer) = await SignInAllAsync();
        var integration = await CreateIntegrationAsync(admin, config: """{"preset":"demo"}""");
        await WaitForDevicesAsync(viewer, 20);

        var reauth = await StartReauthAsync(admin, integration.Id);
        await ContinueAsync(admin, reauth.FlowId, """{"preset":"basic","seed":42}""");
        await AdvanceClockAsync(admin, 3);
        await ContinueAsync(admin, reauth.FlowId, "{}");

        IReadOnlyList<DeviceSummaryDto> devices = [];
        await RecordingRealtimePublisher.WaitUntilAsync(
            async () => (devices = (await viewer.GetFromJsonAsync<DeviceSummaryDto[]>("/api/devices", Ct))!).Count(d => d.IsOrphaned) == 15,
            "15 demo devices are orphaned");
        devices.Count.ShouldBe(20);
        devices.Where(d => !d.IsOrphaned).Select(d => d.DeviceClass).ShouldBe(
            [DeviceClasses.AirConditioner, DeviceClasses.Outlet, DeviceClasses.Lock, DeviceClasses.Door, DeviceClasses.Light],
            ignoreOrder: true);
        devices.Where(d => d.IsOrphaned).ShouldAllBe(d => d.Availability == "Unknown");
    }

    [Fact]
    public async Task Flows_belong_to_the_admin_who_started_them()
    {
        var (admin, member, _) = await SignInAllAsync();
        await admin.CreateUserAsync("second", Password, Roles.Admin, Ct);
        using var second = await Factory.LoginAsync("second", Password, Ct);

        using var byMember = await member.PostAsJsonAsync("/api/integrations/flows", new StartFlowRequest("simulated"), Ct);
        await byMember.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "auth.forbidden");
        var form = await StartAsync(admin, "simulated");
        form.FlowId.Version.ShouldBe(4, "flow ids are random (122 bits), not time-ordered");

        using var bySecond = await ContinueRawAsync(second, form.FlowId, "{}");
        await bySecond.ShouldBeProblemAsync(HttpStatusCode.NotFound, "flows.not_found");
        using var cancelBySecond = await second.DeleteAsync($"/api/integrations/flows/{form.FlowId}", Ct);
        await cancelBySecond.ShouldBeProblemAsync(HttpStatusCode.NotFound, "flows.not_found");

        using var cancel = await admin.DeleteAsync($"/api/integrations/flows/{form.FlowId}", Ct);
        await cancel.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
        using var afterCancel = await ContinueRawAsync(admin, form.FlowId, "{}");
        await afterCancel.ShouldBeProblemAsync(HttpStatusCode.NotFound, "flows.not_found");
        (await QueryDbAsync(db => db.Integrations.CountAsync(Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task Unknown_connectors_integrations_and_flows_are_rejected()
    {
        var (admin, _, _) = await SignInAllAsync();

        using var unknownConnector = await admin.PostAsJsonAsync("/api/integrations/flows", new StartFlowRequest("nope"), Ct);
        await unknownConnector.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation.failed");
        (await unknownConnector.Content.ReadAsStringAsync(Ct)).ShouldContain("\"connectorKey\":[\"connectors.not_found\"]");
        using var unknownIntegration = await admin.PostAsync($"/api/integrations/{Guid.NewGuid()}/flows/reauth", null, Ct);
        await unknownIntegration.ShouldBeProblemAsync(HttpStatusCode.NotFound, "integrations.not_found");
        using var unknownFlow = await ContinueRawAsync(admin, Guid.NewGuid(), "{}");
        await unknownFlow.ShouldBeProblemAsync(HttpStatusCode.NotFound, "flows.not_found");
        var form = await StartAsync(admin, "simulated");
        using var notAnObject = await admin.PostAsJsonAsync($"/api/integrations/flows/{form.FlowId}", new { input = 5 }, Ct);
        await notAnObject.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation.failed");
    }

    [Fact]
    public async Task Admins_rename_disable_and_enable_integrations()
    {
        var (admin, member, viewer) = await SignInAllAsync();
        var integration = await CreateIntegrationAsync(admin);
        await WaitForDevicesAsync(viewer, 5);

        using var byMember = await member.PutAsJsonAsync($"/api/integrations/{integration.Id}", new UpdateIntegrationRequest("X", false), Ct);
        await byMember.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "auth.forbidden");
        using var disable = await admin.PutAsJsonAsync($"/api/integrations/{integration.Id}", new UpdateIntegrationRequest(" Casa ", false), Ct);
        await disable.ShouldHaveStatusAsync(HttpStatusCode.OK);
        var disabled = (await disable.Content.ReadFromJsonAsync<IntegrationDto>(Ct))!;
        (disabled.Name, disabled.Enabled, disabled.Status, disabled.DeviceCount).ShouldBe(("Casa", false, "Stopped", 5));
        await RecordingRealtimePublisher.WaitUntilAsync(
            async () => (await viewer.GetFromJsonAsync<DeviceSummaryDto[]>("/api/devices", Ct))!.All(d => d.Availability == "Unknown"),
            "the devices of a disabled integration are Unknown");

        using var enable = await admin.PutAsJsonAsync($"/api/integrations/{integration.Id}", new UpdateIntegrationRequest("Casa", true), Ct);
        await enable.ShouldHaveStatusAsync(HttpStatusCode.OK);
        await WaitForDevicesAsync(viewer, 5);
        await WaitForStatusAsync(viewer, integration.Id, "Ok");
        (await AuditEntriesAsync("integrations.updated")).Count.ShouldBe(2);
        using var unknown = await admin.PutAsJsonAsync($"/api/integrations/{Guid.NewGuid()}", new UpdateIntegrationRequest("X", true), Ct);
        await unknown.ShouldBeProblemAsync(HttpStatusCode.NotFound, "integrations.not_found");
    }

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task<FlowStepDto> StartAsync(HttpClient admin, string connectorKey)
    {
        using var response = await admin.PostAsJsonAsync("/api/integrations/flows", new StartFlowRequest(connectorKey), Ct);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<FlowStepDto>(Ct))!;
    }

    private static async Task<FlowStepDto> StartReauthAsync(HttpClient admin, Guid integrationId)
    {
        using var response = await admin.PostAsync($"/api/integrations/{integrationId}/flows/reauth", null, Ct);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<FlowStepDto>(Ct))!;
    }

    private static Task<HttpResponseMessage> ContinueRawAsync(HttpClient admin, Guid flowId, string input) =>
        admin.PostAsJsonAsync($"/api/integrations/flows/{flowId}", new ContinueFlowRequest(Json.Of(input)), Ct);

    private static async Task<FlowStepDto> ContinueAsync(HttpClient admin, Guid flowId, string input)
    {
        using var response = await ContinueRawAsync(admin, flowId, input);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<FlowStepDto>(Ct))!;
    }

    private static async Task AdvanceClockAsync(HttpClient client, double seconds)
    {
        using var response = await client.PostAsJsonAsync("/api/_test/clock", new { advanceSeconds = seconds }, Ct);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
    }

    private static Task WaitForStatusAsync(HttpClient client, Guid integrationId, string status) =>
        RecordingRealtimePublisher.WaitUntilAsync(
            async () => (await client.GetFromJsonAsync<IntegrationDto[]>("/api/integrations", Ct))!.Single(i => i.Id == integrationId).Status == status,
            $"integration {integrationId} is {status}");
}

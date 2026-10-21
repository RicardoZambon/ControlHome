using System.Net;
using System.Net.Http.Json;
using ControlHome.Application.Capabilities;
using ControlHome.Application.Devices;
using ControlHome.Application.Integrations;
using ControlHome.Domain.Capabilities;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Api.IntegrationTests.Devices;

[Collection(DevicesCollection.Name)]
public sealed class IntegrationApiTests(DevicesApiFactory factory) : DevicesTestBase(factory)
{
    [Fact]
    public async Task Everyone_signed_in_reads_the_capability_and_connector_catalogues()
    {
        var (_, _, viewer) = await SignInAllAsync();

        var capabilities = (await viewer.GetFromJsonAsync<CapabilityDto[]>("/api/capabilities", Ct))!;
        var connectors = (await viewer.GetFromJsonAsync<ConnectorDto[]>("/api/connectors", Ct))!;

        capabilities.Select(c => c.Id).ShouldBe(CapabilityCatalog.All.Select(c => c.Id));
        capabilities.Single(c => c.Id == CapabilityIds.Lock).Commands.Single(c => c.Name == "unlock").SafetyCritical.ShouldBeTrue();
        var simulated = connectors.Single(c => c.Key == "simulated");
        (simulated.DisplayName, simulated.Category, simulated.Icon).ShouldBe((new LocalizedTextDto("Simulated home", "Casa simulada"), "virtual", "flask-conical"));
        simulated.ConfigFields.Select(f => f.Key).ShouldBe(["preset", "seed", "simulateReauth"]);
        simulated.ConfigFields[0].Schema.ShouldBeOfType<EnumSchema>().Values.ShouldBe(["basic", "demo"]);
        (simulated.Features.PushInventory, simulated.Features.SetupFlow, simulated.Features.Reauth).ShouldBe((true, true, true));
        connectors.Single(c => c.Key == CanaryConnectorFactory.Key).ConfigFields.Single(f => f.Key == "token").Secret.ShouldBeTrue();
    }

    [Fact]
    public async Task Only_admins_create_and_delete_integrations()
    {
        var (admin, member, viewer) = await SignInAllAsync();

        using var byMember = await member.PostAsJsonAsync("/api/integrations", new CreateIntegrationRequest("simulated", "Mine", null), Ct);
        await byMember.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "auth.forbidden");
        var created = await CreateIntegrationAsync(admin);
        (await viewer.GetFromJsonAsync<IntegrationDto[]>("/api/integrations", Ct))!.ShouldHaveSingleItem().Id.ShouldBe(created.Id);
        using var deleteByMember = await member.DeleteAsync($"/api/integrations/{created.Id}", Ct);
        await deleteByMember.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "auth.forbidden");
        using var deleteUnknown = await admin.DeleteAsync($"/api/integrations/{Guid.NewGuid()}", Ct);
        await deleteUnknown.ShouldBeProblemAsync(HttpStatusCode.NotFound, "integrations.not_found");
        (await AuditEntriesAsync("integrations.created")).ShouldHaveSingleItem().TargetId.ShouldBe(created.Id.ToString());
    }

    [Fact]
    public async Task Unknown_connectors_and_invalid_configs_are_rejected()
    {
        var (admin, _, _) = await SignInAllAsync();

        using var unknown = await admin.PostAsJsonAsync("/api/integrations", new CreateIntegrationRequest("nope", "X", null), Ct);
        using var badPreset = await admin.PostAsJsonAsync(
            "/api/integrations",
            new CreateIntegrationRequest("simulated", "X", Json.Of("""{"preset":"castle","seed":-1}""")),
            Ct);
        using var noName = await admin.PostAsJsonAsync("/api/integrations", new CreateIntegrationRequest("simulated", " ", null), Ct);

        await unknown.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation.failed");
        (await unknown.Content.ReadAsStringAsync(Ct)).ShouldContain("\"connectorKey\":[\"connectors.not_found\"]");
        await badPreset.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "integrations.invalid_config");
        var errors = (await badPreset.Content.ReadAsStringAsync(Ct));
        errors.ShouldContain("\"config.preset\":[\"commands.argument_not_allowed\"]");
        errors.ShouldContain("\"config.seed\":[\"commands.argument_out_of_range\"]");
        await noName.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation.failed");
        (await QueryDbAsync(db => db.Integrations.CountAsync(Ct))).ShouldBe(0);
    }
}

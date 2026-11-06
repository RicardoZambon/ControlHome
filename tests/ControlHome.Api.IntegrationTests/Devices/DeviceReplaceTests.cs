using System.Net;
using System.Net.Http.Json;
using ControlHome.Api.IntegrationTests.Floors;
using ControlHome.Application.Devices;
using ControlHome.Application.Placements;
using ControlHome.Domain.Devices;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Api.IntegrationTests.Devices;

/// <summary>Likely duplicates across integrations are flagged; replace moves a device's settings, placements and history.</summary>
[Collection(DevicesCollection.Name)]
public sealed class DeviceReplaceTests(DevicesApiFactory factory) : DevicesTestBase(factory)
{
    [Fact]
    public async Task The_same_hardware_in_two_integrations_is_flagged_as_a_likely_duplicate()
    {
        var (admin, _, viewer) = await SignInAllAsync();
        var first = await CreateIntegrationAsync(admin, config: """{"preset":"basic","seed":7}""");
        await WaitForDevicesAsync(viewer, 5);
        await CreateIntegrationAsync(admin, config: """{"preset":"basic","seed":8}""");
        var unrelated = await WaitForDevicesAsync(viewer, 10);
        unrelated.ShouldAllBe(d => d.PossibleDuplicateIds.Count == 0, "other seeds are other hardware");

        // The same seed is the same MAC addresses and serial numbers.
        var copy = await CreateIntegrationAsync(admin, config: """{"preset":"basic","seed":7}""");
        var devices = await WaitForDevicesAsync(viewer, 15);

        var original = devices.Single(d => d.IntegrationId == first.Id && d.DeviceClass == DeviceClasses.Light);
        var duplicate = devices.Single(d => d.IntegrationId == copy.Id && d.DeviceClass == DeviceClasses.Light);
        original.PossibleDuplicateIds.ShouldBe([duplicate.Id]);
        duplicate.PossibleDuplicateIds.ShouldBe([original.Id]);
        (await viewer.GetFromJsonAsync<DeviceDetailDto>($"/api/devices/{original.Id}", Ct))!.PossibleDuplicateIds.ShouldBe([duplicate.Id]);
        devices.Count(d => d.PossibleDuplicateIds.Count > 0).ShouldBe(10);
    }

    [Fact]
    public async Task Replacing_moves_settings_placements_favorites_and_history_then_deletes_the_old_device()
    {
        var (admin, _, viewer) = await SignInAllAsync();
        var floor = await admin.CreateFloorAsync(Ct);
        floor = await admin.SaveLayoutOkAsync(floor, [FloorApi.Room("Sala", FloorApi.Square(0, 0, 400))], Ct);
        var sala = floor.Rooms.Single().Id;
        var first = await CreateIntegrationAsync(admin, config: """{"preset":"basic","seed":7}""");
        var oldLight = DeviceOfClass(await WaitForDevicesAsync(viewer, 5), DeviceClasses.Light);
        var oldEndpoint = MainEndpoint(oldLight).Id;
        using (var rename = await admin.PutAsJsonAsync($"/api/devices/{oldLight.Id}", new UpdateDeviceRequest("Lustre", "lamp-ceiling", null), Ct))
        {
            await rename.ShouldHaveStatusAsync(HttpStatusCode.OK);
        }

        using (var favorite = await admin.PutAsJsonAsync($"/api/endpoints/{oldEndpoint}/favorite", new SetFavoriteRequest(true), Ct))
        {
            await favorite.ShouldHaveStatusAsync(HttpStatusCode.OK);
        }

        using (var place = await admin.PutAsJsonAsync("/api/placements", new PlaceRequest(oldEndpoint, null, floor.Id, 100, 100), Ct))
        {
            await place.ShouldHaveStatusAsync(HttpStatusCode.OK);
        }

        var oldOn = AttributeId(oldLight, "switch", "on");
        await RecordingRealtimePublisher.WaitUntilAsync(
            async () => await QueryDbAsync(db => db.StateLog.AnyAsync(s => s.AttributeId == oldOn, Ct)),
            "the old light has history");
        var oldHistory = await QueryDbAsync(db => db.StateLog.CountAsync(s => s.AttributeId == oldOn, Ct));

        await CreateIntegrationAsync(admin, config: """{"preset":"basic","seed":9}""");
        var newLight = (await WaitForDevicesAsync(viewer, 10)).Single(d => d.IntegrationId != first.Id && d.DeviceClass == DeviceClasses.Light);
        var newOn = AttributeId(newLight, "switch", "on");

        using var response = await admin.PostAsJsonAsync($"/api/devices/{oldLight.Id}/replace", new ReplaceDeviceRequest(newLight.Id), Ct);

        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        var replaced = (await response.Content.ReadFromJsonAsync<DeviceDetailDto>(Ct))!;
        (replaced.Id, replaced.Name, replaced.Icon).ShouldBe((newLight.Id, "Lustre", "lamp-ceiling"));
        var endpoint = replaced.Endpoints.Single();
        (endpoint.Favorite, endpoint.RoomId).ShouldBe((true, (int?)sala));
        using (var gone = await viewer.GetAsync($"/api/devices/{oldLight.Id}", Ct))
        {
            await gone.ShouldBeProblemAsync(HttpStatusCode.NotFound, "devices.not_found");
        }

        (await viewer.GetFloorAsync(floor.Id, Ct)).Placements.ShouldHaveSingleItem().EndpointId.ShouldBe(endpoint.Id);
        // The old light's history moved; the new light's own first values are written behind, so they can land later.
        await RecordingRealtimePublisher.WaitUntilAsync(
            async () => await QueryDbAsync(db => db.StateLog.CountAsync(s => s.AttributeId == newOn, Ct)) >= oldHistory + 1,
            "the new light has the old history and its own");
        (await QueryDbAsync(db => db.StateLog.CountAsync(s => s.AttributeId == oldOn, Ct))).ShouldBe(0);
        (await AuditEntriesAsync("devices.replaced")).ShouldHaveSingleItem().TargetId.ShouldBe(oldLight.Id.ToString());

        // The new device keeps working.
        await SendAcceptedAsync(admin, endpoint.Id, "switch", "turnOff");
        await WaitForStateAsync(viewer, newOn, ControlHome.Domain.Capabilities.StateValue.False);
    }

    [Fact]
    public async Task Invalid_replacements_are_rejected()
    {
        var (admin, member, viewer) = await SignInAllAsync();
        await CreateIntegrationAsync(admin);
        var devices = await WaitForDevicesAsync(viewer, 5);
        var light = DeviceOfClass(devices, DeviceClasses.Light);
        var plug = DeviceOfClass(devices, DeviceClasses.Outlet);

        using var byMember = await member.PostAsJsonAsync($"/api/devices/{light.Id}/replace", new ReplaceDeviceRequest(plug.Id), Ct);
        await byMember.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "auth.forbidden");
        using var unknownOld = await admin.PostAsJsonAsync($"/api/devices/{Guid.NewGuid()}/replace", new ReplaceDeviceRequest(plug.Id), Ct);
        await unknownOld.ShouldBeProblemAsync(HttpStatusCode.NotFound, "devices.not_found");
        using var same = await admin.PostAsJsonAsync($"/api/devices/{light.Id}/replace", new ReplaceDeviceRequest(light.Id), Ct);
        await same.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation.failed");
        (await same.Content.ReadAsStringAsync(Ct)).ShouldContain("\"newDeviceId\":[\"devices.replace_same\"]");
        using var unknownNew = await admin.PostAsJsonAsync($"/api/devices/{light.Id}/replace", new ReplaceDeviceRequest(Guid.NewGuid()), Ct);
        (await unknownNew.Content.ReadAsStringAsync(Ct)).ShouldContain("\"newDeviceId\":[\"devices.not_found\"]");
        (await QueryDbAsync(db => db.Devices.CountAsync(Ct))).ShouldBe(5);
    }
}

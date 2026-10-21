using System.Net;
using System.Net.Http.Json;
using ControlHome.Api.IntegrationTests.Floors;
using ControlHome.Application.Devices;
using ControlHome.Application.Realtime;
using ControlHome.Domain.Devices;

namespace ControlHome.Api.IntegrationTests.Devices;

/// <summary>User fields of devices and endpoints: Admins edit, Members favorite, Viewers read.</summary>
[Collection(DevicesCollection.Name)]
public sealed class DeviceAdminTests(DevicesApiFactory factory) : DevicesTestBase(factory)
{
    [Fact]
    public async Task Admins_rename_devices_and_set_their_icon_and_room()
    {
        var (admin, member, viewer) = await SignInAllAsync();
        var floor = await admin.CreateFloorAsync(Ct);
        floor = await admin.SaveLayoutOkAsync(floor, [FloorApi.Room("Sala", FloorApi.Square(0, 0, 400))], Ct);
        var room = floor.Rooms.Single().Id;
        await CreateIntegrationAsync(admin);
        var light = DeviceOfClass(await WaitForDevicesAsync(viewer, 5), DeviceClasses.Light);
        Factory.Realtime.Clear();

        using var response = await admin.PutAsJsonAsync($"/api/devices/{light.Id}", new UpdateDeviceRequest("  Lustre  ", "lamp-ceiling", room), Ct);

        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        var detail = (await response.Content.ReadFromJsonAsync<DeviceDetailDto>(Ct))!;
        (detail.Name, detail.Icon, detail.RoomId).ShouldBe(("Lustre", "lamp-ceiling", (int?)room));
        detail.Endpoints.Single().EffectiveRoomId.ShouldBe(room);
        (await viewer.GetFromJsonAsync<DeviceSummaryDto[]>($"/api/devices?roomId={room}", Ct))!.ShouldHaveSingleItem().Id.ShouldBe(light.Id);
        Factory.Realtime.Messages.ShouldContain(m => m.Name == HomeHubMessages.DevicesChanged);
        (await AuditEntriesAsync("devices.updated")).ShouldHaveSingleItem().TargetId.ShouldBe(light.Id.ToString());

        using var byMember = await member.PutAsJsonAsync($"/api/devices/{light.Id}", new UpdateDeviceRequest("X", null, null), Ct);
        await byMember.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "auth.forbidden");
        using var unknownRoom = await admin.PutAsJsonAsync($"/api/devices/{light.Id}", new UpdateDeviceRequest("X", null, 987_654), Ct);
        await unknownRoom.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation.failed");
        (await unknownRoom.Content.ReadAsStringAsync(Ct)).ShouldContain("\"roomId\":[\"rooms.not_found\"]");
        using var blank = await admin.PutAsJsonAsync($"/api/devices/{light.Id}", new UpdateDeviceRequest("  ", null, null), Ct);
        await blank.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation.failed");
        using var unknown = await admin.PutAsJsonAsync($"/api/devices/{Guid.NewGuid()}", new UpdateDeviceRequest("X", null, null), Ct);
        await unknown.ShouldBeProblemAsync(HttpStatusCode.NotFound, "devices.not_found");
    }

    [Fact]
    public async Task Admins_change_endpoints_and_members_favorite_them()
    {
        var (admin, member, viewer) = await SignInAllAsync();
        await CreateIntegrationAsync(admin);
        var plug = DeviceOfClass(await WaitForDevicesAsync(viewer, 5), DeviceClasses.Outlet);
        var endpoint = MainEndpoint(plug).Id;

        using var update = await admin.PutAsJsonAsync($"/api/endpoints/{endpoint}", new UpdateEndpointRequest("Coffee", "coffee", null, Hidden: true), Ct);
        using var favorite = await member.PutAsJsonAsync($"/api/endpoints/{endpoint}/favorite", new SetFavoriteRequest(true), Ct);
        using var viewerFavorite = await viewer.PutAsJsonAsync($"/api/endpoints/{endpoint}/favorite", new SetFavoriteRequest(false), Ct);
        using var memberUpdate = await member.PutAsJsonAsync($"/api/endpoints/{endpoint}", new UpdateEndpointRequest(null, null, null, false), Ct);

        await update.ShouldHaveStatusAsync(HttpStatusCode.OK);
        var updated = (await update.Content.ReadFromJsonAsync<EndpointDto>(Ct))!;
        (updated.Name, updated.Icon, updated.Hidden).ShouldBe(("Coffee", "coffee", true));
        await favorite.ShouldHaveStatusAsync(HttpStatusCode.OK);
        (await favorite.Content.ReadFromJsonAsync<EndpointDto>(Ct))!.Favorite.ShouldBeTrue();
        await viewerFavorite.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "auth.forbidden");
        await memberUpdate.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "auth.forbidden");
        (await AuditEntriesAsync("endpoints.updated")).ShouldHaveSingleItem();
        (await AuditEntriesAsync("endpoints.favorite_changed")).ShouldHaveSingleItem().ActorName.ShouldBe("member");

        using var missing = await member.PutAsJsonAsync($"/api/endpoints/{Guid.NewGuid()}/favorite", new SetFavoriteRequest(true), Ct);
        await missing.ShouldBeProblemAsync(HttpStatusCode.NotFound, "endpoints.not_found");
    }

    [Fact]
    public async Task Devices_can_be_filtered()
    {
        var (admin, _, viewer) = await SignInAllAsync();
        var integration = await CreateIntegrationAsync(admin);
        await WaitForDevicesAsync(viewer, 5);

        (await viewer.GetFromJsonAsync<DeviceSummaryDto[]>("/api/devices?capability=lock", Ct))!.ShouldHaveSingleItem().DeviceClass.ShouldBe(DeviceClasses.Lock);
        (await viewer.GetFromJsonAsync<DeviceSummaryDto[]>($"/api/devices?integrationId={integration.Id}", Ct))!.Length.ShouldBe(5);
        (await viewer.GetFromJsonAsync<DeviceSummaryDto[]>($"/api/devices?integrationId={Guid.NewGuid()}", Ct))!.ShouldBeEmpty();
        (await viewer.GetFromJsonAsync<DeviceSummaryDto[]>("/api/devices?q=no-such-device", Ct))!.ShouldBeEmpty();
    }
}

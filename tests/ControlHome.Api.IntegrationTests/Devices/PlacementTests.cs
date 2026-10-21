using System.Net;
using System.Net.Http.Json;
using ControlHome.Api.IntegrationTests.Floors;
using ControlHome.Application.Devices;
using ControlHome.Application.Floors;
using ControlHome.Application.Placements;
using ControlHome.Application.Realtime;
using ControlHome.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using static ControlHome.Api.IntegrationTests.Floors.FloorApi;

namespace ControlHome.Api.IntegrationTests.Devices;

/// <summary>Endpoints and devices on the floor plans: placing assigns the room by point-in-polygon; Admins only.</summary>
[Collection(DevicesCollection.Name)]
public sealed class PlacementTests(DevicesApiFactory factory) : DevicesTestBase(factory)
{
    [Fact]
    public async Task Placing_an_endpoint_puts_it_in_the_room_under_the_point_and_moving_it_follows()
    {
        var (admin, _, viewer) = await SignInAllAsync();
        var (floor, sala, cozinha) = await TwoRoomFloorAsync(admin);
        await CreateIntegrationAsync(admin);
        var light = DeviceOfClass(await WaitForDevicesAsync(viewer, 5), DeviceClasses.Light);
        var endpoint = MainEndpoint(light).Id;
        Factory.Realtime.Clear();

        var placed = await PlaceOkAsync(admin, new PlaceRequest(endpoint, null, floor.Id, 150, 200));
        (placed.FloorId, placed.EndpointId, placed.DeviceId, placed.XCm, placed.YCm, placed.RoomId)
            .ShouldBe((floor.Id, (Guid?)endpoint, (Guid?)null, 150d, 200d, (int?)sala));
        (await EndpointAsync(viewer, light.Id)).EffectiveRoomId.ShouldBe(sala);
        Factory.Realtime.Messages.ShouldContain(m => m.Name == HomeHubMessages.DevicesChanged);

        var moved = await PlaceOkAsync(admin, new PlaceRequest(endpoint, null, floor.Id, 550, 100));
        moved.Id.ShouldBe(placed.Id);
        moved.RoomId.ShouldBe(cozinha);
        (await EndpointAsync(viewer, light.Id)).RoomId.ShouldBe(cozinha);

        var outside = await PlaceOkAsync(admin, new PlaceRequest(endpoint, null, floor.Id, 1500, 1200));
        outside.RoomId.ShouldBeNull();
        (await EndpointAsync(viewer, light.Id)).RoomId.ShouldBeNull();

        var fetched = await viewer.GetFloorAsync(floor.Id, Ct);
        fetched.Placements.ShouldHaveSingleItem().ShouldBe(outside);
        (await viewer.GetFromJsonAsync<PlacementDto[]>("/api/placements", Ct))!.ShouldBe([outside]);
        (await viewer.GetFromJsonAsync<PlacementDto[]>($"/api/placements?floorId={floor.Id + 1000}", Ct))!.ShouldBeEmpty();
        (await AuditEntriesAsync("placements.saved")).Count.ShouldBe(3);
    }

    [Fact]
    public async Task Placing_a_whole_device_sets_the_device_room()
    {
        var (admin, _, viewer) = await SignInAllAsync();
        var (floor, _, cozinha) = await TwoRoomFloorAsync(admin);
        await CreateIntegrationAsync(admin);
        var plug = DeviceOfClass(await WaitForDevicesAsync(viewer, 5), DeviceClasses.Outlet);

        var placed = await PlaceOkAsync(admin, new PlaceRequest(null, plug.Id, floor.Id, 500, 250));

        (placed.DeviceId, placed.EndpointId, placed.RoomId).ShouldBe(((Guid?)plug.Id, (Guid?)null, (int?)cozinha));
        var device = (await viewer.GetFromJsonAsync<DeviceDetailDto>($"/api/devices/{plug.Id}", Ct))!;
        device.RoomId.ShouldBe(cozinha);
        device.Endpoints.Single().EffectiveRoomId.ShouldBe(cozinha);
    }

    [Fact]
    public async Task Invalid_placements_are_rejected_with_field_errors()
    {
        var (admin, _, viewer) = await SignInAllAsync();
        var (floor, _, _) = await TwoRoomFloorAsync(admin);
        await CreateIntegrationAsync(admin);
        var light = DeviceOfClass(await WaitForDevicesAsync(viewer, 5), DeviceClasses.Light);
        var endpoint = MainEndpoint(light).Id;

        using var both = await admin.PutAsJsonAsync("/api/placements", new PlaceRequest(endpoint, light.Id, floor.Id, 10, 10), Ct);
        using var none = await admin.PutAsJsonAsync("/api/placements", new PlaceRequest(null, null, floor.Id, 10, 10), Ct);
        using var unknownFloor = await admin.PutAsJsonAsync("/api/placements", new PlaceRequest(endpoint, null, 987_654, 10, 10), Ct);
        using var unknownEndpoint = await admin.PutAsJsonAsync("/api/placements", new PlaceRequest(Guid.NewGuid(), null, floor.Id, 10, 10), Ct);
        using var unknownDevice = await admin.PutAsJsonAsync("/api/placements", new PlaceRequest(null, Guid.NewGuid(), floor.Id, 10, 10), Ct);
        using var outOfBounds = await admin.PutAsJsonAsync("/api/placements", new PlaceRequest(endpoint, null, floor.Id, 2000.5, -1), Ct);
        using var offFloor = await admin.PutAsJsonAsync("/api/placements", new PlaceRequest(endpoint, null, floor.Id, 2001, 1501), Ct);

        (await both.ReadFieldErrorsAsync(Ct)).Keys.ShouldBe(["endpointId"]);
        (await none.ReadFieldErrorsAsync(Ct)).Keys.ShouldBe(["endpointId"]);
        (await unknownFloor.ReadFieldErrorsAsync(Ct))["floorId"].ShouldBe(["floors.not_found"]);
        (await unknownEndpoint.ReadFieldErrorsAsync(Ct))["endpointId"].ShouldBe(["endpoints.not_found"]);
        (await unknownDevice.ReadFieldErrorsAsync(Ct))["deviceId"].ShouldBe(["devices.not_found"]);
        (await outOfBounds.ReadFieldErrorsAsync(Ct)).Keys.ShouldBe(["yCm"]);
        var off = await offFloor.ReadFieldErrorsAsync(Ct);
        off["xCm"].ShouldBe(["geometry.out_of_bounds"]);
        off["yCm"].ShouldBe(["geometry.out_of_bounds"]);
        (await QueryDbAsync(db => db.Placements.CountAsync(Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task Only_admins_place_and_remove()
    {
        var (admin, member, viewer) = await SignInAllAsync();
        var (floor, _, _) = await TwoRoomFloorAsync(admin);
        await CreateIntegrationAsync(admin);
        var light = DeviceOfClass(await WaitForDevicesAsync(viewer, 5), DeviceClasses.Light);
        var request = new PlaceRequest(MainEndpoint(light).Id, null, floor.Id, 100, 100);

        using var byMember = await member.PutAsJsonAsync("/api/placements", request, Ct);
        using var byViewer = await viewer.PutAsJsonAsync("/api/placements", request, Ct);
        await byMember.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "auth.forbidden");
        await byViewer.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "auth.forbidden");

        var placed = await PlaceOkAsync(admin, request);
        using var deleteByMember = await member.DeleteAsync($"/api/placements/{placed.Id}", Ct);
        await deleteByMember.ShouldBeProblemAsync(HttpStatusCode.Forbidden, "auth.forbidden");
        using var delete = await admin.DeleteAsync($"/api/placements/{placed.Id}", Ct);
        await delete.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
        using var again = await admin.DeleteAsync($"/api/placements/{placed.Id}", Ct);
        await again.ShouldBeProblemAsync(HttpStatusCode.NotFound, "placements.not_found");

        // Taking it off the plan keeps its room.
        (await EndpointAsync(viewer, light.Id)).RoomId.ShouldNotBeNull();
        (await AuditEntriesAsync("placements.deleted")).ShouldHaveSingleItem().TargetId.ShouldBe(placed.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_floor_with_placed_devices_cannot_be_deleted_or_shrunk_below_them()
    {
        var (admin, _, viewer) = await SignInAllAsync();
        var (floor, _, _) = await TwoRoomFloorAsync(admin);
        await CreateIntegrationAsync(admin);
        var plug = DeviceOfClass(await WaitForDevicesAsync(viewer, 5), DeviceClasses.Outlet);
        var placed = await PlaceOkAsync(admin, new PlaceRequest(null, plug.Id, floor.Id, 1800, 1400));
        floor = await admin.GetFloorAsync(floor.Id, Ct);

        using var delete = await admin.DeleteAsync(FloorUrl(floor.Id), Ct);
        await delete.ShouldBeProblemAsync(HttpStatusCode.Conflict, "floors.not_empty");
        using var shrink = await admin.PutAsJsonAsync(
            FloorUrl(floor.Id),
            new UpdateFloorRequest(floor.Name, floor.Level, 1700, 1300, floor.GridCm, null, floor.RowVersion),
            Ct);
        var errors = await shrink.ReadFieldErrorsAsync(Ct);
        errors["widthCm"].ShouldBe(["geometry.out_of_bounds"]);
        errors["heightCm"].ShouldBe(["geometry.out_of_bounds"]);

        using (var remove = await admin.DeleteAsync($"/api/placements/{placed.Id}", Ct))
        {
            await remove.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
        }

        using var deleteAgain = await admin.DeleteAsync(FloorUrl(floor.Id), Ct);
        await deleteAgain.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Saving_a_layout_moves_placed_devices_whose_room_changed()
    {
        var (admin, _, viewer) = await SignInAllAsync();
        var (floor, sala, cozinha) = await TwoRoomFloorAsync(admin);
        await CreateIntegrationAsync(admin);
        var devices = await WaitForDevicesAsync(viewer, 5);
        var light = DeviceOfClass(devices, DeviceClasses.Light);
        var plug = DeviceOfClass(devices, DeviceClasses.Outlet);
        await PlaceOkAsync(admin, new PlaceRequest(MainEndpoint(light).Id, null, floor.Id, 300, 300));
        await PlaceOkAsync(admin, new PlaceRequest(null, plug.Id, floor.Id, 500, 100));

        // The plug's room is overridden by hand; the layout change does not touch its point's room, so it keeps that.
        using (var manual = await admin.PutAsJsonAsync($"/api/devices/{plug.Id}", new UpdateDeviceRequest(plug.Name, null, sala), Ct))
        {
            await manual.ShouldHaveStatusAsync(HttpStatusCode.OK);
        }

        // Sala shrinks to the left half: the light's point (300, 300) is now in the new "Hall".
        floor = await admin.GetFloorAsync(floor.Id, Ct);
        var saved = await admin.SaveLayoutOkAsync(
            floor,
            [
                Room("Sala", Square(0, 0, 200), id: sala),
                Room("Cozinha", [P(400, 0), P(700, 0), P(700, 300), P(400, 300)], id: cozinha),
                Room("Hall", [P(200, 0), P(400, 0), P(400, 400), P(200, 400)]),
            ],
            Ct);
        var hall = saved.Rooms.Single(r => r.Name == "Hall").Id;

        saved.Placements.Single(p => p.EndpointId is not null).RoomId.ShouldBe(hall);
        (await EndpointAsync(viewer, light.Id)).RoomId.ShouldBe(hall);
        (await viewer.GetFromJsonAsync<DeviceDetailDto>($"/api/devices/{plug.Id}", Ct))!.RoomId.ShouldBe(sala);
    }

    [Fact]
    public async Task Deleting_an_integration_removes_its_placements()
    {
        var (admin, _, viewer) = await SignInAllAsync();
        var (floor, _, _) = await TwoRoomFloorAsync(admin);
        var integration = await CreateIntegrationAsync(admin);
        var devices = await WaitForDevicesAsync(viewer, 5);
        await PlaceOkAsync(admin, new PlaceRequest(MainEndpoint(DeviceOfClass(devices, DeviceClasses.Light)).Id, null, floor.Id, 100, 100));
        await PlaceOkAsync(admin, new PlaceRequest(null, DeviceOfClass(devices, DeviceClasses.Lock).Id, floor.Id, 200, 100));

        using var delete = await admin.DeleteAsync($"/api/integrations/{integration.Id}", Ct);

        await delete.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
        (await QueryDbAsync(db => db.Placements.CountAsync(Ct))).ShouldBe(0);
        (await admin.GetFloorAsync(floor.Id, Ct)).Placements.ShouldBeEmpty();
    }

    /// <summary>A floor with Sala (0..400 × 0..400) and Cozinha (400..700 × 0..300).</summary>
    private static async Task<(FloorDto Floor, int Sala, int Cozinha)> TwoRoomFloorAsync(HttpClient admin)
    {
        var floor = await admin.CreateFloorAsync(Ct);
        floor = await admin.SaveLayoutOkAsync(
            floor,
            [Room("Sala", Square(0, 0, 400)), Room("Cozinha", [P(400, 0), P(700, 0), P(700, 300), P(400, 300)])],
            Ct);
        return (floor, floor.Rooms.Single(r => r.Name == "Sala").Id, floor.Rooms.Single(r => r.Name == "Cozinha").Id);
    }

    private static async Task<PlacementDto> PlaceOkAsync(HttpClient admin, PlaceRequest request)
    {
        using var response = await admin.PutAsJsonAsync("/api/placements", request, Ct);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<PlacementDto>(Ct))!;
    }

    private static async Task<EndpointDto> EndpointAsync(HttpClient client, Guid deviceId) =>
        (await client.GetFromJsonAsync<DeviceDetailDto>($"/api/devices/{deviceId}", Ct))!.Endpoints.Single();
}

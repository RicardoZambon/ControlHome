using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ControlHome.Api.IntegrationTests.Files;
using ControlHome.Application.Floors;
using ControlHome.Domain.Security;
using ControlHome.TestSupport;
using Microsoft.EntityFrameworkCore;
using static ControlHome.Api.IntegrationTests.Floors.FloorApi;

namespace ControlHome.Api.IntegrationTests.Floors;

[Collection(AuthCollection.Name)]
public sealed class FloorTests(AuthApiFactory factory) : ResetAppTestBase<AuthApiFactory>(factory)
{
    private const string Password = "a long enough password";

    [Fact]
    public async Task Admin_creates_a_floor_with_default_size()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);

        using var response = await admin.PostAsJsonAsync("/api/floors", new CreateFloorRequest("  Térreo  ", 0, null, null), Ct);

        await response.ShouldHaveStatusAsync(HttpStatusCode.Created, Ct);
        var floor = (await response.Content.ReadFromJsonAsync<FloorDto>(Ct))!;
        response.Headers.Location!.OriginalString.ShouldBe(FloorUrl(floor.Id));
        floor.Name.ShouldBe("Térreo");
        floor.Level.ShouldBe(0);
        (floor.WidthCm, floor.HeightCm, floor.GridCm).ShouldBe((2000, 1500, 50));
        floor.Background.ShouldBeNull();
        floor.Rooms.ShouldBeEmpty();
        Convert.FromBase64String(floor.RowVersion).Length.ShouldBe(8);
        (await admin.GetFloorAsync(floor.Id, Ct)).ShouldBeEquivalentTo(floor);
        (await AuditEntriesAsync("floors.created")).ShouldHaveSingleItem().TargetId.ShouldBe(floor.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Floors_are_listed_by_level_with_their_room_count()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var upper = await admin.CreateFloorAsync(Ct, "Superior", 1, widthCm: 1200, heightCm: 900);
        var ground = await admin.CreateFloorAsync(Ct, "Térreo", 0);
        await admin.SaveLayoutOkAsync(ground, [Room("Sala", Square(0, 0, 400)), Room("Cozinha", Square(400, 0, 300))], Ct);

        var floors = (await admin.GetFromJsonAsync<FloorSummaryDto[]>("/api/floors", Ct))!;

        floors.ShouldBe([new FloorSummaryDto(ground.Id, "Térreo", 0, 2), new FloorSummaryDto(upper.Id, "Superior", 1, 0)]);
        (upper.WidthCm, upper.HeightCm).ShouldBe((1200, 900));
    }

    [Fact]
    public async Task Unknown_floor_is_404_floors_not_found()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);

        using var get = await admin.GetAsync(FloorUrl(4242), Ct);
        using var put = await admin.PutAsJsonAsync(FloorUrl(4242), new UpdateFloorRequest("X", 0, 2000, 1500, 50, null, "AAAAAAAAB9E="), Ct);
        using var layout = await admin.SaveLayoutAsync(4242, "AAAAAAAAB9E=", [], Ct);
        using var delete = await admin.DeleteAsync(FloorUrl(4242), Ct);

        foreach (var response in new[] { get, put, layout, delete })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await response.ReadProblemCodeAsync(Ct)).ShouldBe("floors.not_found");
        }
    }

    [Fact]
    public async Task Admin_updates_a_floor_and_gets_a_new_row_version()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var floor = await admin.CreateFloorAsync(Ct);

        using var response = await admin.PutAsJsonAsync(
            FloorUrl(floor.Id),
            new UpdateFloorRequest("Ground floor", -1, 2500, 1800, 25, null, floor.RowVersion),
            Ct);

        await response.ShouldHaveStatusAsync(HttpStatusCode.OK, Ct);
        var updated = (await response.Content.ReadFromJsonAsync<FloorDto>(Ct))!;
        (updated.Name, updated.Level, updated.WidthCm, updated.HeightCm, updated.GridCm).ShouldBe(("Ground floor", -1, 2500, 1800, 25));
        updated.RowVersion.ShouldNotBe(floor.RowVersion);
        (await admin.GetFloorAsync(floor.Id, Ct)).ShouldBeEquivalentTo(updated);
        var audit = (await AuditEntriesAsync("floors.updated")).ShouldHaveSingleItem();
        using var details = JsonDocument.Parse(audit.DetailsJson!);
        details.RootElement.GetProperty("fields").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(["name", "level", "widthCm", "heightCm", "gridCm"], ignoreOrder: true);
    }

    [Fact]
    public async Task Update_with_a_stale_row_version_is_409_floors_concurrency()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var floor = await admin.CreateFloorAsync(Ct);
        using (var first = await admin.PutAsJsonAsync(FloorUrl(floor.Id), new UpdateFloorRequest("First", 0, 2000, 1500, 50, null, floor.RowVersion), Ct))
        {
            await first.ShouldHaveStatusAsync(HttpStatusCode.OK, Ct);
        }

        using var second = await admin.PutAsJsonAsync(FloorUrl(floor.Id), new UpdateFloorRequest("Second", 0, 2000, 1500, 50, null, floor.RowVersion), Ct);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await second.ReadProblemCodeAsync(Ct)).ShouldBe("floors.concurrency");
        (await admin.GetFloorAsync(floor.Id, Ct)).Name.ShouldBe("First");
    }

    [Fact]
    public async Task Floor_cannot_shrink_below_its_rooms()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var floor = await admin.SaveLayoutOkAsync(await admin.CreateFloorAsync(Ct), [Room("Sala", [P(0, 0), P(1500, 0), P(1500, 1200), P(0, 1200)])], Ct);

        using var response = await admin.PutAsJsonAsync(
            FloorUrl(floor.Id),
            new UpdateFloorRequest(floor.Name, 0, 1400, 1100, 50, null, floor.RowVersion),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var errors = await response.ReadFieldErrorsAsync(Ct);
        errors.Keys.ShouldBe(["widthCm", "heightCm"], ignoreOrder: true);
        errors["widthCm"].ShouldBe(["geometry.out_of_bounds"]);
        errors["heightCm"].ShouldBe(["geometry.out_of_bounds"]);
        (await admin.GetFloorAsync(floor.Id, Ct)).WidthCm.ShouldBe(2000);
    }

    [Fact]
    public async Task Admin_sets_and_removes_a_background_image()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var floor = await admin.CreateFloorAsync(Ct);
        var file = await admin.UploadAsync(TestImages.Png(800, 600), "plan.png", Ct);
        var background = new FloorBackgroundDto(file.Id, 0.6, -25.5, 10, 2.5);

        using var set = await admin.PutAsJsonAsync(FloorUrl(floor.Id), new UpdateFloorRequest(floor.Name, 0, 2000, 1500, 50, background, floor.RowVersion), Ct);

        await set.ShouldHaveStatusAsync(HttpStatusCode.OK, Ct);
        var withBackground = (await set.Content.ReadFromJsonAsync<FloorDto>(Ct))!;
        withBackground.Background.ShouldBe(background);
        (await admin.GetFloorAsync(floor.Id, Ct)).Background.ShouldBe(background);

        using var removed = await admin.PutAsJsonAsync(
            FloorUrl(floor.Id),
            new UpdateFloorRequest(floor.Name, 0, 2000, 1500, 50, null, withBackground.RowVersion),
            Ct);

        await removed.ShouldHaveStatusAsync(HttpStatusCode.OK, Ct);
        (await admin.GetFloorAsync(floor.Id, Ct)).Background.ShouldBeNull();
    }

    [Fact]
    public async Task Background_must_be_an_uploaded_file()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var floor = await admin.CreateFloorAsync(Ct);

        using var response = await admin.PutAsJsonAsync(
            FloorUrl(floor.Id),
            new UpdateFloorRequest(floor.Name, 0, 2000, 1500, 50, new FloorBackgroundDto(Guid.NewGuid(), 1, 0, 0, 1), floor.RowVersion),
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var errors = await response.ReadFieldErrorsAsync(Ct);
        errors.Keys.ShouldBe(["background.fileId"]);
        errors["background.fileId"].ShouldBe(["files.not_found"]);
    }

    [Fact]
    public async Task Invalid_floor_fields_are_400_validation_failed()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var floor = await admin.CreateFloorAsync(Ct);

        using var create = await admin.PostAsJsonAsync("/api/floors", new CreateFloorRequest("", 500, 50, 1500), Ct);
        using var update = await admin.PutAsJsonAsync(
            FloorUrl(floor.Id),
            new UpdateFloorRequest("Ok", 0, 2000, 1500, 0, new FloorBackgroundDto(Guid.Empty, 1.5, 0, 0, 0), "not-a-row-version"),
            Ct);

        create.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await create.ReadFieldErrorsAsync(Ct)).Keys.ShouldBe(["name", "level", "widthCm"], ignoreOrder: true);
        update.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await update.ReadFieldErrorsAsync(Ct)).Keys.ShouldBe(
            ["gridCm", "background.fileId", "background.opacity", "background.cmPerPx", "rowVersion"],
            ignoreOrder: true);
    }

    [Fact]
    public async Task Admin_deletes_a_floor_with_its_rooms()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var floor = await admin.SaveLayoutOkAsync(await admin.CreateFloorAsync(Ct), [Room("Sala", Square(0, 0, 400))], Ct);

        using var response = await admin.DeleteAsync(FloorUrl(floor.Id), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var get = await admin.GetAsync(FloorUrl(floor.Id), Ct);
        get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await QueryDbAsync(db => db.Rooms.CountAsync(Ct))).ShouldBe(0);
        (await AuditEntriesAsync("floors.deleted")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Members_and_viewers_read_but_cannot_change_floors()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var floor = await admin.SaveLayoutOkAsync(await admin.CreateFloorAsync(Ct), [Room("Sala", Square(0, 0, 400))], Ct);
        var file = await admin.UploadAsync(TestImages.Png(40, 30), "small.png", Ct);
        await admin.CreateUserAsync("member", Password, Roles.Member, Ct);
        await admin.CreateUserAsync("viewer", Password, Roles.Viewer, Ct);

        foreach (var userName in new[] { "member", "viewer" })
        {
            using var client = await Factory.LoginAsync(userName, Password, Ct);

            (await client.GetFromJsonAsync<FloorSummaryDto[]>("/api/floors", Ct))!.ShouldHaveSingleItem().Id.ShouldBe(floor.Id);
            (await client.GetFloorAsync(floor.Id, Ct)).Rooms.ShouldHaveSingleItem().Name.ShouldBe("Sala");
            (await client.GetFromJsonAsync<RoomListItemDto[]>("/api/rooms", Ct))!.ShouldHaveSingleItem().Name.ShouldBe("Sala");
            using (var download = await client.GetAsync($"/api/files/{file.Id}", Ct))
            {
                download.StatusCode.ShouldBe(HttpStatusCode.OK);
            }

            using var create = await client.PostAsJsonAsync("/api/floors", new CreateFloorRequest("Mine", 2, null, null), Ct);
            using var update = await client.PutAsJsonAsync(FloorUrl(floor.Id), new UpdateFloorRequest("Mine", 0, 2000, 1500, 50, null, floor.RowVersion), Ct);
            using var layout = await client.SaveLayoutAsync(floor.Id, floor.RowVersion, [], Ct);
            using var delete = await client.DeleteAsync(FloorUrl(floor.Id), Ct);
            using var upload = await client.PostAsync("/api/files", TestFiles.Form(TestImages.Png(10, 10), "x.png"), Ct);
            foreach (var response in new[] { create, update, layout, delete, upload })
            {
                response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{userName}: {response.RequestMessage!.Method} {response.RequestMessage.RequestUri}");
                (await response.ReadProblemCodeAsync(Ct)).ShouldBe("auth.forbidden");
            }
        }

        var unchanged = await admin.GetFloorAsync(floor.Id, Ct);
        unchanged.ShouldBeEquivalentTo(floor);
    }

    [Fact]
    public async Task Anonymous_gets_401()
    {
        using var client = Factory.CreateApiClient();

        using var floors = await client.GetAsync("/api/floors", Ct);
        using var rooms = await client.GetAsync("/api/rooms", Ct);
        using var file = await client.GetAsync($"/api/files/{Guid.NewGuid()}", Ct);

        floors.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        rooms.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        file.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Rooms_are_listed_by_floor_level_and_room_order()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var upper = await admin.CreateFloorAsync(Ct, "Superior", 1);
        var ground = await admin.CreateFloorAsync(Ct, "Térreo", 0);
        upper = await admin.SaveLayoutOkAsync(upper, [Room("Quarto", Square(0, 0, 300))], Ct);
        ground = await admin.SaveLayoutOkAsync(ground, [Room("Sala", Square(0, 0, 300)), Room("Cozinha", Square(300, 0, 300))], Ct);

        var rooms = (await admin.GetFromJsonAsync<RoomListItemDto[]>("/api/rooms", Ct))!;

        rooms.ShouldBe(
        [
            new RoomListItemDto(ground.Rooms[0].Id, "Sala", ground.Id, "Térreo"),
            new RoomListItemDto(ground.Rooms[1].Id, "Cozinha", ground.Id, "Térreo"),
            new RoomListItemDto(upper.Rooms[0].Id, "Quarto", upper.Id, "Superior"),
        ]);
    }
}

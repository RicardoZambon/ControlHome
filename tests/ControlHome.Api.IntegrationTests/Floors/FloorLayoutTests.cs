using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ControlHome.Application.Floors;
using ControlHome.TestSupport;
using Microsoft.EntityFrameworkCore;
using static ControlHome.Api.IntegrationTests.Floors.FloorApi;

namespace ControlHome.Api.IntegrationTests.Floors;

[Collection(AuthCollection.Name)]
public sealed class FloorLayoutTests(AuthApiFactory factory) : ResetAppTestBase<AuthApiFactory>(factory)
{
    [Fact]
    public async Task Saving_a_layout_creates_the_rooms_in_order()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var floor = await admin.CreateFloorAsync(Ct);
        PointCmDto[] lShape = [P(0, 0), P(100, 0), P(100, 100), P(200, 100), P(200, 200), P(0, 200)];

        var saved = await admin.SaveLayoutOkAsync(
            floor,
            [
                Room("Sala", Square(0, 0, 400), icon: "sofa", colorToken: "room-3"),
                Room("Corredor", lShape, icon: "mdi:door-open", colorToken: "room-8"),
            ],
            Ct);

        saved.RowVersion.ShouldNotBe(floor.RowVersion);
        saved.Rooms.Count.ShouldBe(2);
        saved.Rooms.ShouldAllBe(r => r.Id > 0);
        var (sala, corredor) = (saved.Rooms[0], saved.Rooms[1]);
        (sala.Name, sala.Icon, sala.ColorToken, sala.SortOrder, sala.AreaM2).ShouldBe(("Sala", "sofa", "room-3", 0, 16.0));
        sala.Polygon.ShouldBe(Square(0, 0, 400));
        (corredor.Name, corredor.Icon, corredor.ColorToken, corredor.SortOrder, corredor.AreaM2).ShouldBe(("Corredor", "mdi:door-open", "room-8", 1, 3.0));
        corredor.Polygon.ShouldBe(lShape);
        (await admin.GetFloorAsync(floor.Id, Ct)).ShouldBeEquivalentTo(saved);
    }

    [Fact]
    public async Task Saving_again_updates_listed_rooms_adds_new_ones_and_deletes_the_rest()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var floor = await admin.SaveLayoutOkAsync(
            await admin.CreateFloorAsync(Ct),
            [Room("Sala", Square(0, 0, 400)), Room("Cozinha", Square(400, 0, 300))],
            Ct);
        var (sala, cozinha) = (floor.Rooms[0], floor.Rooms[1]);

        var saved = await admin.SaveLayoutOkAsync(
            floor,
            [
                Room("Banheiro", Square(800, 0, 200), icon: "bath", colorToken: "room-5"),
                Room("Sala de estar", Square(0, 0, 500), id: sala.Id, icon: "armchair", colorToken: "room-2"),
            ],
            Ct);

        saved.Rooms.Count.ShouldBe(2);
        var banheiro = saved.Rooms[0];
        banheiro.Id.ShouldNotBeOneOf(sala.Id, cozinha.Id);
        (banheiro.Name, banheiro.SortOrder, banheiro.AreaM2).ShouldBe(("Banheiro", 0, 4.0));
        var renamed = saved.Rooms[1];
        (renamed.Id, renamed.Name, renamed.Icon, renamed.ColorToken, renamed.SortOrder, renamed.AreaM2)
            .ShouldBe((sala.Id, "Sala de estar", "armchair", "room-2", 1, 25.0));
        renamed.Polygon.ShouldBe(Square(0, 0, 500));
        (await QueryDbAsync(db => db.Rooms.AnyAsync(r => r.Id == cozinha.Id, Ct))).ShouldBeFalse();
        (await admin.GetFloorAsync(floor.Id, Ct)).ShouldBeEquivalentTo(saved);

        var audit = (await AuditEntriesAsync("floors.layout_saved"))[^1];
        using var details = JsonDocument.Parse(audit.DetailsJson!);
        (details.RootElement.GetProperty("rooms").GetInt32(), details.RootElement.GetProperty("added").GetInt32(), details.RootElement.GetProperty("removed").GetInt32())
            .ShouldBe((2, 1, 1));
    }

    [Fact]
    public async Task An_empty_layout_deletes_every_room()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var floor = await admin.SaveLayoutOkAsync(await admin.CreateFloorAsync(Ct), [Room("Sala", Square(0, 0, 400))], Ct);

        var saved = await admin.SaveLayoutOkAsync(floor, [], Ct);

        saved.Rooms.ShouldBeEmpty();
        (await QueryDbAsync(db => db.Rooms.CountAsync(Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task Layout_saved_with_a_stale_row_version_is_409_and_changes_nothing()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var floor = await admin.CreateFloorAsync(Ct);
        var first = await admin.SaveLayoutOkAsync(floor, [Room("Sala", Square(0, 0, 400))], Ct);

        using var stale = await admin.SaveLayoutAsync(floor.Id, floor.RowVersion, [Room("Quarto", Square(0, 0, 300))], Ct);

        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await stale.ReadProblemCodeAsync(Ct)).ShouldBe("floors.concurrency");
        (await admin.GetFloorAsync(floor.Id, Ct)).ShouldBeEquivalentTo(first);
    }

    [Fact]
    public async Task Of_two_admins_saving_the_same_version_at_once_exactly_one_wins()
    {
        using var first = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        using var second = await Factory.LoginAsync(AuthTestExtensions.AdminUserName, AuthTestExtensions.AdminPassword, Ct);
        var floor = await first.CreateFloorAsync(Ct);

        for (var round = 0; round < 3; round++)
        {
            var current = await first.GetFloorAsync(floor.Id, Ct);
            var saves = await Task.WhenAll(
                first.SaveLayoutAsync(floor.Id, current.RowVersion, [Room($"A{round}", Square(0, 0, 300)), Room($"B{round}", Square(300, 0, 300))], Ct),
                second.SaveLayoutAsync(floor.Id, current.RowVersion, [Room($"C{round}", Square(0, 400, 300))], Ct));

            try
            {
                saves.Select(r => r.StatusCode).OrderBy(s => s).ShouldBe([HttpStatusCode.OK, HttpStatusCode.Conflict]);
                var loser = saves.Single(r => r.StatusCode == HttpStatusCode.Conflict);
                (await loser.ReadProblemCodeAsync(Ct)).ShouldBe("floors.concurrency");
                var winner = (await saves.Single(r => r.StatusCode == HttpStatusCode.OK).Content.ReadFromJsonAsync<FloorDto>(Ct))!;
                (await first.GetFloorAsync(floor.Id, Ct)).ShouldBeEquivalentTo(winner);
            }
            finally
            {
                foreach (var response in saves)
                {
                    response.Dispose();
                }
            }
        }
    }

    [Fact]
    public async Task Invalid_polygons_are_field_errors_with_geometry_codes()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var floor = await admin.CreateFloorAsync(Ct); // 2000 × 1500 cm

        using var response = await admin.SaveLayoutAsync(
            floor.Id,
            floor.RowVersion,
            [
                Room("Sala", Square(0, 0, 400)),
                Room("Gravata", [P(500, 0), P(800, 300), P(800, 0), P(500, 300)]),
                Room("Fora", Square(1900, 0, 300)),
                Room("Linha", [P(0, 500), P(100, 500)]),
                Room("Plana", [P(0, 600), P(100, 600), P(200, 600)]),
                Room("Fresta", [P(0, 700), P(100, 700), P(100, 700.005)]),
                Room("Negativa", [P(-1, 800), P(100, 800), P(100, 900)]),
                Room("Repetida", [P(0, 1000), P(100, 1000), P(100, 1000), P(100, 1100)]),
            ],
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var errors = await response.ReadFieldErrorsAsync(Ct);
        errors.Keys.ShouldBe(
            ["rooms[1].polygon", "rooms[2].polygon", "rooms[3].polygon", "rooms[4].polygon", "rooms[5].polygon", "rooms[6].polygon", "rooms[7].polygon"],
            ignoreOrder: true);
        errors["rooms[1].polygon"].ShouldBe(["geometry.self_intersecting"]);
        errors["rooms[2].polygon"].ShouldBe(["geometry.out_of_bounds"]);
        errors["rooms[3].polygon"].ShouldBe(["geometry.too_few_points"]);
        errors["rooms[4].polygon"].ShouldBe(["geometry.zero_area"]);
        errors["rooms[5].polygon"].ShouldBe(["geometry.zero_area"]);
        errors["rooms[6].polygon"].ShouldBe(["geometry.out_of_bounds"]);
        errors["rooms[7].polygon"].ShouldBe(["geometry.self_intersecting"]);
        var unchanged = await admin.GetFloorAsync(floor.Id, Ct);
        unchanged.Rooms.ShouldBeEmpty();
        unchanged.RowVersion.ShouldBe(floor.RowVersion);
    }

    [Fact]
    public async Task Room_on_the_floor_edge_is_inside()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var floor = await admin.CreateFloorAsync(Ct, widthCm: 1000, heightCm: 800);

        var saved = await admin.SaveLayoutOkAsync(floor, [Room("Tudo", [P(0, 0), P(1000, 0), P(1000, 800), P(0, 800)])], Ct);

        saved.Rooms.ShouldHaveSingleItem().AreaM2.ShouldBe(80.0);
    }

    [Fact]
    public async Task Too_many_points_is_a_geometry_field_error()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var floor = await admin.CreateFloorAsync(Ct);
        var circle = Enumerable.Range(0, FloorRules.MaxPolygonPoints + 1)
            .Select(i => P(700 + (500 * Math.Cos(i * 2 * Math.PI / (FloorRules.MaxPolygonPoints + 1))), 700 + (500 * Math.Sin(i * 2 * Math.PI / (FloorRules.MaxPolygonPoints + 1)))))
            .ToArray();

        using var response = await admin.SaveLayoutAsync(floor.Id, floor.RowVersion, [Room("Redonda", circle)], Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadFieldErrorsAsync(Ct))["rooms[0].polygon"].ShouldBe(["geometry.too_many_points"]);
    }

    [Fact]
    public async Task Room_of_another_floor_is_rooms_not_found()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var other = await admin.SaveLayoutOkAsync(await admin.CreateFloorAsync(Ct, "Superior", 1), [Room("Quarto", Square(0, 0, 300))], Ct);
        var floor = await admin.CreateFloorAsync(Ct);

        using var response = await admin.SaveLayoutAsync(
            floor.Id,
            floor.RowVersion,
            [Room("Roubado", Square(0, 0, 300), id: other.Rooms[0].Id), Room("Inexistente", Square(400, 0, 300), id: 987_654)],
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var errors = await response.ReadFieldErrorsAsync(Ct);
        errors["rooms[0].id"].ShouldBe(["rooms.not_found"]);
        errors["rooms[1].id"].ShouldBe(["rooms.not_found"]);
        (await admin.GetFloorAsync(other.Id, Ct)).ShouldBeEquivalentTo(other);
    }

    [Fact]
    public async Task Invalid_room_fields_are_400_validation_failed()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var floor = await admin.SaveLayoutOkAsync(await admin.CreateFloorAsync(Ct), [Room("Sala", Square(0, 0, 400))], Ct);
        var sala = floor.Rooms[0];

        using var response = await admin.SaveLayoutAsync(
            floor.Id,
            floor.RowVersion,
            [
                Room("", Square(0, 0, 100), icon: "Not An Icon", colorToken: "red"),
                Room("Sala", Square(0, 0, 400), id: sala.Id),
                Room("Sala again", Square(0, 0, 400), id: sala.Id),
            ],
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadFieldErrorsAsync(Ct)).Keys.ShouldBe(
            ["rooms[0].name", "rooms[0].icon", "rooms[0].colorToken", "rooms"],
            ignoreOrder: true);
    }

    [Fact]
    public async Task Missing_rooms_or_row_version_is_400()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var floor = await admin.CreateFloorAsync(Ct);

        using var response = await admin.PutAsJsonAsync(LayoutUrl(floor.Id), new { }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadFieldErrorsAsync(Ct)).Keys.ShouldBe(["rooms", "rowVersion"], ignoreOrder: true);
    }
}

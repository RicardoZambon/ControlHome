using System.Net;
using System.Net.Http.Json;
using ControlHome.Api.Features.Demo;
using ControlHome.Api.IntegrationTests.Floors;
using ControlHome.Application.Dashboard;
using ControlHome.Application.Demo;
using ControlHome.Application.Devices;
using ControlHome.Application.Floors;
using ControlHome.Application.Integrations;
using ControlHome.Connectors.Simulated;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Geometry;
using ControlHome.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ControlHome.Api.IntegrationTests.Devices;

/// <summary>The demo home (setup option, test hook, <c>--seed-demo</c>), the dashboard summary over it, and camera stills.</summary>
[Collection(DevicesCollection.Name)]
public sealed class DemoHomeTests(DevicesApiFactory factory) : DevicesTestBase(factory)
{
    [Fact]
    public async Task Setup_can_load_the_demo_home_named_in_the_home_language()
    {
        using var admin = await Factory.CompleteSetupAsync(AuthTestExtensions.SetupRequest() with { LoadDemoHome = true }, Ct);

        var floors = (await admin.GetFromJsonAsync<FloorSummaryDto[]>("/api/floors", Ct))!;
        floors.Select(f => (f.Name, f.Level, f.RoomCount)).ShouldBe([("Térreo", 0, 4), ("Superior", 1, 4)]);
        (await admin.GetFromJsonAsync<IntegrationDto[]>("/api/integrations", Ct))!.ShouldHaveSingleItem().Name.ShouldBe("Casa de demonstração");
        var devices = await WaitForDevicesAsync(admin, 20);
        devices.Select(d => d.Name).ShouldContain("Luz da sala");
        devices.Select(d => d.Name).ShouldContain("Caixa d'água");

        var ground = await admin.GetFloorAsync(floors[0].Id, Ct);
        var upper = await admin.GetFloorAsync(floors[1].Id, Ct);
        (ground.Placements.Count + upper.Placements.Count).ShouldBe(DemoHome.Placements.Count);
        var sala = ground.Rooms.Single(r => r.Name == "Sala").Id;
        var light = devices.Single(d => d.Name == "Luz da sala");
        MainEndpoint(light).EffectiveRoomId.ShouldBe(sala);
        var porch = devices.Single(d => d.Name == "Interruptor da sala").Endpoints.Single(e => e.Key == "l3");
        (porch.Name, porch.RoomId, porch.EffectiveRoomId).ShouldBe(("Varanda", (int?)null, (int?)sala), "outside every room: its device's room");
        ground.Placements.Single(p => p.EndpointId == porch.Id).RoomId.ShouldBeNull();
        devices.SelectMany(d => d.Endpoints).Count(e => e.Favorite).ShouldBe(6);
        (await AuditEntriesAsync("demo.loaded")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_demo_home_is_loaded_only_into_an_empty_home()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);

        using (var seed = await admin.PostAsync("/api/_test/seed-demo?locale=en", null, Ct))
        {
            await seed.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
        }

        using var again = await admin.PostAsync("/api/_test/seed-demo", null, Ct);
        await again.ShouldBeProblemAsync(HttpStatusCode.Conflict, "demo.not_empty");
        (await admin.GetFromJsonAsync<FloorSummaryDto[]>("/api/floors", Ct))!.Select(f => f.Name).ShouldBe(["Ground floor", "Upper floor"]);
        (await WaitForDevicesAsync(admin, 20)).Select(d => d.Name).ShouldContain("Living room light");
    }

    [Fact]
    public async Task The_seed_demo_command_loads_the_demo_home_once()
    {
        var (first, output, _) = await RunSeedDemoAsync("pt-BR");
        var (second, _, error) = await RunSeedDemoAsync();

        first.ShouldBe(SeedDemoCommand.ExitSuccess);
        output.ShouldContain("2 floors, 8 rooms, 20 devices");
        second.ShouldBe(SeedDemoCommand.ExitNotEmpty);
        error.ShouldContain("Nothing was changed");
        (await QueryDbAsync(db => db.Devices.CountAsync(Ct))).ShouldBe(20);
        (await QueryDbAsync(db => db.Floors.OrderBy(f => f.Level).Select(f => f.Name).ToListAsync(Ct))).ShouldBe(["Térreo", "Superior"]);
        (await RunSeedDemoAsync("fr")).ExitCode.ShouldBe(SeedDemoCommand.ExitUsage);
    }

    [Fact]
    public async Task The_dashboard_sums_up_the_home()
    {
        using var admin = await Factory.CompleteSetupAsync(cancellationToken: Ct);
        var empty = (await admin.GetFromJsonAsync<DashboardDto>("/api/dashboard", Ct))!;
        (empty.Rooms.Count, empty.Integrations.Count, empty.FloorCount, empty.DeviceCount, empty.Power.Watts).ShouldBe((0, 0, 0, 0, (double?)null));

        using (var seed = await admin.PostAsync("/api/_test/seed-demo?locale=en", null, Ct))
        {
            await seed.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
        }

        var devices = await WaitForDevicesAsync(admin, 20);
        var dashboard = (await admin.GetFromJsonAsync<DashboardDto>("/api/dashboard", Ct))!;
        var states = (await admin.GetFromJsonAsync<AttributeStateDto[]>("/api/state", Ct))!.ToDictionary(s => s.AttributeId, s => s.Value);

        (dashboard.FloorCount, dashboard.DeviceCount, dashboard.Rooms.Count).ShouldBe((2, 20, 8));
        dashboard.Integrations.ShouldHaveSingleItem().Name.ShouldBe("Demo home");
        dashboard.FavoriteEndpointIds.Count.ShouldBe(6);
        dashboard.OfflineDevices.ShouldBeEmpty();
        var allPower = devices.SelectMany(d => d.Endpoints).SelectMany(e => e.Capabilities).Where(c => c.CapabilityId == "power")
            .SelectMany(c => c.Attributes).Select(a => a.AttributeId).ToList();
        dashboard.Power.AttributeIds.ShouldBe(allPower, ignoreOrder: true);
        dashboard.Power.Watts.ShouldNotBeNull().ShouldBeGreaterThan(0);

        var living = dashboard.Rooms.Single(r => r.Name == "Living room");
        living.DeviceCount.ShouldBe(6); // light, TV, door sensor, lock, blinds, wall switch
        living.ActiveDeviceCount.ShouldBeGreaterThanOrEqualTo(2, "the light and the TV are on");
        living.ActivityAttributes.ShouldContain(a => a.ActiveValues.Contains(StateValue.True));
        living.TemperatureC.ShouldBeNull();
        var bedroom = dashboard.Rooms.Single(r => r.Name == "Bedroom");
        bedroom.TemperatureAttributeIds.ShouldHaveSingleItem();
        bedroom.TemperatureC.ShouldNotBeNull().ShouldBe(states[bedroom.TemperatureAttributeIds[0]].N!.Value, tolerance: 0.16, "the AC moves 0.1 °C per tick");
        var kitchen = dashboard.Rooms.Single(r => r.Name == "Kitchen");
        kitchen.PowerAttributeIds.Count.ShouldBe(2);
        kitchen.PowerW.ShouldNotBeNull().ShouldBeGreaterThan(0);
        dashboard.Rooms.Single(r => r.Name == "Laundry").ActiveDeviceCount.ShouldBe(1, "the washer is running");
    }

    [Fact]
    public async Task Cameras_serve_their_still_behind_the_app_authorization()
    {
        var (admin, _, viewer) = await SignInAllAsync();
        var integration = await CreateIntegrationAsync(admin, config: """{"preset":"demo"}""");
        var devices = await WaitForDevicesAsync(viewer, 20);
        var camera = MainEndpoint(devices.Single(d => d.Name == "Gate camera")).Id;
        var light = MainEndpoint(devices.Single(d => d.Name == "Living room light")).Id;

        using var snapshot = await viewer.GetAsync($"/api/endpoints/{camera}/camera/snapshot", Ct);
        await snapshot.ShouldHaveStatusAsync(HttpStatusCode.OK);
        snapshot.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        snapshot.Headers.CacheControl!.NoStore.ShouldBeTrue();
        (await snapshot.Content.ReadAsByteArrayAsync(Ct))[..4].ShouldBe(new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        using var notACamera = await viewer.GetAsync($"/api/endpoints/{light}/camera/snapshot", Ct);
        await notACamera.ShouldBeProblemAsync(HttpStatusCode.NotFound, "cameras.no_snapshot");
        using var unknown = await viewer.GetAsync($"/api/endpoints/{Guid.NewGuid()}/camera/snapshot", Ct);
        await unknown.ShouldBeProblemAsync(HttpStatusCode.NotFound, "endpoints.not_found");
        using var anonymous = Factory.CreateApiClient();
        using var notSignedIn = await anonymous.GetAsync($"/api/endpoints/{camera}/camera/snapshot", Ct);
        notSignedIn.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using (var disable = await admin.PutAsJsonAsync($"/api/integrations/{integration.Id}", new UpdateIntegrationRequest("Demo", false), Ct))
        {
            await disable.ShouldHaveStatusAsync(HttpStatusCode.OK);
        }

        using var stopped = await viewer.GetAsync($"/api/endpoints/{camera}/camera/snapshot", Ct);
        await stopped.ShouldBeProblemAsync(HttpStatusCode.ServiceUnavailable, "commands.unavailable");
    }

    private async Task<(int ExitCode, string Output, string Error)> RunSeedDemoAsync(params string[] extra)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:ControlHome"] = Factory.ConnectionString,
            ["ControlHome:DataPath"] = Factory.DataPath,
            ["Database:StartupTimeout"] = "00:00:05",
        };
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = await SeedDemoCommand.RunAsync(
            [SeedDemoCommand.Name, .. extra],
            output,
            error,
            builder =>
            {
                builder.Configuration.AddInMemoryCollection(settings);
                builder.Logging.ClearProviders();
            },
            Ct);
        return (exitCode, output.ToString(), error.ToString());
    }
}

/// <summary>The demo home's data fits the Simulated demo preset and the floor-plan rules.</summary>
public sealed class DemoHomeDataTests
{
    [Fact]
    public void Every_demo_device_is_placed_once_inside_its_floor_and_every_room_is_valid()
    {
        var devices = new SimulatedConnectorFactory().DescribeDevices(Json.Of(DemoHome.IntegrationConfigJson));
        var floors = DemoHome.Floors.ToDictionary(f => f.Key);

        foreach (var floor in DemoHome.Floors)
        {
            foreach (var room in floor.Rooms)
            {
                RoomPolygonRules.Validate(room.Polygon, floor.WidthCm, floor.HeightCm).ShouldBeNull(room.NameEn);
            }
        }

        DemoHome.Floors.SelectMany(f => f.Rooms).Count().ShouldBe(8);
        foreach (var placement in DemoHome.Placements)
        {
            var device = devices.SingleOrDefault(d => d.ExternalId == placement.ExternalId).ShouldNotBeNull(placement.ExternalId);
            if (placement.EndpointKey is { } key)
            {
                device.Endpoints.ShouldContain(e => e.Key == key, placement.ExternalId);
            }

            var floor = floors[placement.FloorKey];
            RoomPolygonRules.IsWithin(new PointCm(placement.XCm, placement.YCm), floor.WidthCm, floor.HeightCm).ShouldBeTrue(placement.ExternalId);
        }

        DemoHome.Placements.Select(p => p.ExternalId).Distinct().ShouldBe(devices.Select(d => d.ExternalId), ignoreOrder: true);
        DemoHome.Placements.GroupBy(p => (p.ExternalId, p.EndpointKey)).ShouldAllBe(g => g.Count() == 1);
        DemoHome.PortugueseDeviceNames.Keys.ShouldBe(devices.Select(d => d.ExternalId), ignoreOrder: true);
        DemoHome.Favorites.ShouldAllBe(f => devices.Any(d => d.ExternalId == f.ExternalId && d.Endpoints.Any(e => e.Key == f.EndpointKey)));
    }
}

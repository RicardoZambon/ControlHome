using ControlHome.Application.Common;
using ControlHome.Application.Devices;
using ControlHome.Application.Floors;
using ControlHome.Application.Identity;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using ControlHome.TestSupport;
using Microsoft.EntityFrameworkCore;
using static ControlHome.Infrastructure.Tests.Devices.DeviceDatabase;

namespace ControlHome.Infrastructure.Tests.Devices;

public sealed class DeviceModelTests(SqlServerFixture sql)
{
    [Fact]
    public async Task The_device_graph_round_trips_with_json_columns_and_typed_state()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CreateAsync(sql, ct);
        var integration = Integration();
        var thermostat = Device(integration.Id, "ac-1", "Bedroom AC", CapabilityIds.Switch, CapabilityIds.Thermostat);
        thermostat.Endpoints[0].Capabilities[1].ConstraintsJson = """{"attributes":{"mode":{"values":["off","cool"]}}}""";
        var generic = new CapabilityDefinition(
            CapabilityIds.Generic,
            1,
            [new AttributeDefinition("childLock", BoolSchema.Instance, null, StateClass.None, AttributeCategory.Primary, false, true)],
            []);
        thermostat.Endpoints[0].Capabilities.Add(new EndpointCapability
        {
            CapabilityId = CapabilityIds.Generic,
            GenericDefinitionJson = CapabilityJson.Serialize(generic),
            Attributes = [new DeviceAttribute { Key = "childLock" }],
        });
        integration.Devices.Add(thermostat);

        await database.WithDbAsync(async db =>
        {
            db.Integrations.Add(integration);
            await db.SaveChangesAsync(ct);
            var mode = Attribute(thermostat, CapabilityIds.Thermostat, "mode");
            var target = Attribute(thermostat, CapabilityIds.Thermostat, "targetTemperature");
            var on = Attribute(thermostat, CapabilityIds.Switch, "on");
            var childLock = thermostat.Endpoints[0].Capabilities[2].Attributes[0];
            AddState(db, mode.Id, StateValue.String("cool"));
            AddState(db, target.Id, StateValue.Number(23.5));
            AddState(db, on.Id, StateValue.True);
            AddState(db, childLock.Id, StateValue.Json(SimulatedJson("""{"since":"2026-10-01","by":["app"]}""")));
            await db.SaveChangesAsync(ct);
        });

        await database.WithDbAsync(async db =>
        {
            var loaded = await db.Devices
                .Include(d => d.Endpoints).ThenInclude(e => e.Capabilities).ThenInclude(c => c.Attributes).ThenInclude(a => a.State)
                .SingleAsync(d => d.Id == thermostat.Id, ct);
            loaded.ExternalId.ShouldBe("ac-1");
            loaded.Availability.ShouldBe(Availability.Online);
            var capabilities = loaded.Endpoints.Single().Capabilities.OrderBy(c => c.Id).ToList();
            capabilities.Select(c => c.CapabilityId).ShouldBe(["switch", "thermostat", "generic"]);
            CapabilityConstraints.Parse(SimulatedJson(capabilities[1].ConstraintsJson!)).For("mode")!.Values.ShouldBe(["off", "cool"]);
            CapabilityJson.Deserialize<CapabilityDefinition>(capabilities[2].GenericDefinitionJson!).ShouldBe(generic);

            var values = capabilities.SelectMany(c => c.Attributes).Where(a => a.State is not null)
                .ToDictionary(a => a.Key, a => a.State!.GetValue());
            values["mode"].ShouldBe(StateValue.String("cool"));
            values["targetTemperature"].ShouldBe(StateValue.Number(23.5));
            values["on"].ShouldBe(StateValue.True);
            values["childLock"].ShouldBe(StateValue.Json(SimulatedJson("""{"since":"2026-10-01","by":["app"]}""")));

            var stored = await db.Integrations.AsNoTracking().SingleAsync(i => i.Id == integration.Id, ct);
            using var config = System.Text.Json.JsonDocument.Parse(stored.ConfigJson);
            config.RootElement.GetProperty("seed").GetInt32().ShouldBe(42);
            stored.Status.ShouldBe(IntegrationStatus.Ok);
        });
    }

    [Fact]
    public async Task Unique_keys_json_validity_and_cascades_are_enforced_by_the_database()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CreateAsync(sql, ct);
        var integration = Integration();
        var light = Device(integration.Id, "light-1", "Light", CapabilityIds.Switch, CapabilityIds.Level);
        integration.Devices.Add(light);
        await database.WithDbAsync(async db =>
        {
            db.Integrations.Add(integration);
            await db.SaveChangesAsync(ct);
            AddState(db, Attribute(light, CapabilityIds.Switch, "on").Id, StateValue.False);
            await db.SaveChangesAsync(ct);
        });

        // (IntegrationId, ExternalId)
        await ShouldViolateAsync(database, db => db.Devices.Add(Device(integration.Id, "light-1", "Same light")));

        // The same external id in another integration is a different device.
        var other = Integration("Other");
        other.Devices.Add(Device(other.Id, "light-1", "Other light", CapabilityIds.Switch));
        await database.WithDbAsync(async db =>
        {
            db.Integrations.Add(other);
            await db.SaveChangesAsync(ct);
        });

        // (DeviceId, Key)
        await ShouldViolateAsync(database, db => db.Endpoints.Add(new Endpoint { Id = Guid.CreateVersion7(), DeviceId = light.Id, Key = "main" }));

        // (EndpointId, CapabilityId)
        await ShouldViolateAsync(database, db => db.EndpointCapabilities.Add(new EndpointCapability
        {
            EndpointId = light.Endpoints[0].Id,
            CapabilityId = CapabilityIds.Switch,
        }));

        // (EndpointCapabilityId, Key)
        await ShouldViolateAsync(database, db => db.DeviceAttributes.Add(new DeviceAttribute
        {
            EndpointCapabilityId = light.Endpoints[0].Capabilities[0].Id,
            Key = "on",
        }));

        // json columns only take JSON.
        await ShouldViolateAsync(database, db =>
        {
            var broken = Integration("Broken");
            broken.ConfigJson = "{not json";
            db.Integrations.Add(broken);
        });

        // Removing an integration removes its devices and everything below them.
        await database.WithDbAsync(async db =>
        {
            await db.Integrations.Where(i => i.Id == integration.Id).ExecuteDeleteAsync(ct);
            (await db.Devices.CountAsync(d => d.IntegrationId == integration.Id, ct)).ShouldBe(0);
            (await db.Endpoints.CountAsync(e => e.DeviceId == light.Id, ct)).ShouldBe(0);
            (await db.AttributeStates.CountAsync(ct)).ShouldBe(0);
            (await db.Devices.CountAsync(ct)).ShouldBe(1); // the other integration's light
        });
    }

    [Fact]
    public async Task The_safety_pin_hash_is_stored_on_the_user()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CreateAsync(sql, ct);
        var user = new AppUser { Id = Guid.CreateVersion7(), UserName = "pin", NormalizedUserName = "PIN", SafetyPinHash = "AQAAAA-hash" };

        await database.WithDbAsync(async db =>
        {
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
        });

        (await database.WithDbAsync(db => db.Users.AsNoTracking().Where(u => u.Id == user.Id).Select(u => u.SafetyPinHash).SingleAsync(ct)))
            .ShouldBe("AQAAAA-hash");
    }

    [Fact]
    public async Task The_query_service_lists_devices_with_attribute_ids_values_and_filters()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CreateAsync(sql, ct);
        var floor = await database.WithAsync<FloorService, FloorDto>(async floors =>
        {
            var created = (await floors.CreateAsync(new CreateFloorRequest("Ground", 0, null, null), ct)).Value!;
            return (await floors.SaveLayoutAsync(
                created.Id,
                new SaveFloorLayoutRequest([Room("Living"), Room("Kitchen", 600)], created.RowVersion),
                ct)).Value!;
        });
        var (living, kitchen) = (floor.Rooms[0].Id, floor.Rooms[1].Id);

        var home = Integration("Home");
        var garage = Integration("Garage", "mqtt");
        var light = Device(home.Id, "light-1", "Living room light", CapabilityIds.Switch, CapabilityIds.Level);
        light.RoomId = living;
        var plug = Device(home.Id, "plug-1", "Coffee plug", CapabilityIds.Switch, CapabilityIds.Power, CapabilityIds.Energy);
        plug.RoomId = living;
        plug.Endpoints[0].RoomId = kitchen; // the endpoint was moved to the kitchen
        var sensor = Device(garage.Id, "contact-1", "Garage door", CapabilityIds.Contact, CapabilityIds.Battery);
        sensor.Manufacturer = "Acme";
        sensor.Endpoints[0].Capabilities[1].Retired = true;
        home.Devices.AddRange([light, plug]);
        garage.Devices.Add(sensor);
        await database.WithDbAsync(async db =>
        {
            db.Integrations.AddRange(home, garage);
            await db.SaveChangesAsync(ct);
            AddState(db, Attribute(light, CapabilityIds.Level, "level").Id, StateValue.Number(40));
            AddState(db, Attribute(plug, CapabilityIds.Power, "power").Id, StateValue.Number(85.5));
            await db.SaveChangesAsync(ct);
        });

        var all = await database.WithAsync<DeviceQueryService, IReadOnlyList<DeviceSummaryDto>>(q => q.ListAsync(new DeviceListQuery(), ct));
        all.Select(d => d.Name).ShouldBe(["Coffee plug", "Garage door", "Living room light"]);
        var lightDto = all.Single(d => d.Id == light.Id);
        lightDto.Availability.ShouldBe("Online");
        var level = lightDto.Endpoints.Single().Capabilities.Single(c => c.CapabilityId == "level").Attributes.Single();
        level.AttributeId.ShouldBe(Attribute(light, CapabilityIds.Level, "level").Id);
        level.Value.ShouldBe(StateValue.Number(40));
        level.ObservedAtUtc.ShouldBe(Now);
        level.Restored.ShouldBeTrue();
        var on = lightDto.Endpoints.Single().Capabilities.Single(c => c.CapabilityId == "switch").Attributes.Single();
        on.Value.ShouldBe(StateValue.Null);
        on.ObservedAtUtc.ShouldBeNull();

        var plugEndpoint = all.Single(d => d.Id == plug.Id).Endpoints.Single();
        plugEndpoint.RoomId.ShouldBe(kitchen);
        plugEndpoint.EffectiveRoomId.ShouldBe(kitchen);
        lightDto.Endpoints.Single().EffectiveRoomId.ShouldBe(living);

        (await List(new DeviceListQuery(RoomId: living))).ShouldBe(["Living room light"]);
        (await List(new DeviceListQuery(RoomId: kitchen))).ShouldBe(["Coffee plug"]);
        (await List(new DeviceListQuery(IntegrationId: garage.Id))).ShouldBe(["Garage door"]);
        (await List(new DeviceListQuery(Capability: "switch"))).ShouldBe(["Coffee plug", "Living room light"]);
        (await List(new DeviceListQuery(Capability: "battery"))).ShouldBeEmpty(); // retired
        (await List(new DeviceListQuery(Q: "acme"))).ShouldBe(["Garage door"]);
        (await List(new DeviceListQuery(Q: "plug", IntegrationId: home.Id))).ShouldBe(["Coffee plug"]);

        var detail = await database.WithAsync<DeviceQueryService, Result<DeviceDetailDto>>(q => q.GetAsync(plug.Id, ct));
        detail.IsSuccess.ShouldBeTrue();
        detail.Value!.IntegrationName.ShouldBe("Home");
        detail.Value.ConnectorKey.ShouldBe("simulated");
        detail.Value.ExternalId.ShouldBe("plug-1");
        detail.Value.Connections["serial"].ShouldBe("SIM-plug-1");
        detail.Value.Endpoints.Single().Capabilities.Select(c => c.CapabilityId).ShouldBe(["energy", "power", "switch"]);

        var missing = await database.WithAsync<DeviceQueryService, Result<DeviceDetailDto>>(q => q.GetAsync(Guid.NewGuid(), ct));
        missing.Error!.Code.ShouldBe(ErrorCodes.DevicesNotFound);

        var states = await database.WithAsync<DeviceQueryService, IReadOnlyList<AttributeStateDto>>(q => q.GetStatesAsync(ct));
        states.Select(s => s.Value).ShouldBe([StateValue.Number(40), StateValue.Number(85.5)], ignoreOrder: true);

        var integrations = await database.WithAsync<DeviceQueryService, IReadOnlyList<IntegrationDto>>(q => q.ListIntegrationsAsync(ct));
        integrations.Select(i => (i.Name, i.ConnectorKey, i.DeviceCount, i.Status)).ShouldBe([("Garage", "mqtt", 1, "Ok"), ("Home", "simulated", 2, "Ok")]);

        async Task<List<string>> List(DeviceListQuery query) =>
            (await database.WithAsync<DeviceQueryService, IReadOnlyList<DeviceSummaryDto>>(q => q.ListAsync(query, ct))).Select(d => d.Name).ToList();
    }

    [Fact]
    public async Task Deleting_rooms_takes_devices_and_endpoints_out_of_them()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CreateAsync(sql, ct);
        var floor = await database.WithAsync<FloorService, FloorDto>(async floors =>
        {
            var created = (await floors.CreateAsync(new CreateFloorRequest("Ground", 0, null, null), ct)).Value!;
            return (await floors.SaveLayoutAsync(
                created.Id,
                new SaveFloorLayoutRequest([Room("Living"), Room("Kitchen", 600)], created.RowVersion),
                ct)).Value!;
        });
        var (living, kitchen) = (floor.Rooms[0].Id, floor.Rooms[1].Id);
        var integration = Integration();
        var light = Device(integration.Id, "light-1", "Light", CapabilityIds.Switch);
        light.RoomId = kitchen;
        light.Endpoints[0].RoomId = kitchen;
        var plug = Device(integration.Id, "plug-1", "Plug", CapabilityIds.Switch);
        plug.RoomId = living;
        plug.Endpoints[0].RoomId = living;
        integration.Devices.AddRange([light, plug]);
        await database.WithDbAsync(async db =>
        {
            db.Integrations.Add(integration);
            await db.SaveChangesAsync(ct);
        });

        // Saving the layout without the kitchen deletes it.
        var saved = await database.WithAsync<FloorService, Result<FloorDto>>(floors => floors.SaveLayoutAsync(
            floor.Id,
            new SaveFloorLayoutRequest([Room("Living", id: living)], floor.RowVersion),
            ct));
        saved.IsSuccess.ShouldBeTrue(saved.Error?.Code);
        (await RoomsAsync()).ShouldBe([(light.Id, null, null), (plug.Id, living, living)]);

        // Deleting the floor deletes the living room too.
        (await database.WithAsync<FloorService, Result>(floors => floors.DeleteAsync(floor.Id, ct))).IsSuccess.ShouldBeTrue();
        (await RoomsAsync()).ShouldBe([(light.Id, null, null), (plug.Id, null, null)]);

        Task<List<(Guid, int?, int?)>> RoomsAsync() => database.WithDbAsync(async db =>
            (await db.Devices.AsNoTracking().Include(d => d.Endpoints).OrderBy(d => d.Name).ToListAsync(ct))
                .Select(d => (d.Id, d.RoomId, d.Endpoints.Single().RoomId))
                .ToList());
    }

    private static void AddState(ControlHome.Infrastructure.Persistence.ControlHomeDbContext db, int attributeId, StateValue value)
    {
        var state = new AttributeState { AttributeId = attributeId };
        state.SetValue(value, Now, Now);
        db.AttributeStates.Add(state);
    }

    private static async Task ShouldViolateAsync(DeviceDatabase database, Action<ControlHome.Infrastructure.Persistence.ControlHomeDbContext> change)
    {
        await database.WithDbAsync(async db =>
        {
            change(db);
            await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        });
    }

    private static RoomInput Room(string name, double x = 0, int? id = null) =>
        new(id, name, "sofa", "room-1", [new PointCmDto(x, 0), new PointCmDto(x + 500, 0), new PointCmDto(x + 500, 400), new PointCmDto(x, 400)]);

    private static System.Text.Json.JsonElement SimulatedJson(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

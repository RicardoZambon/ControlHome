using ControlHome.Application.Realtime;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using ControlHome.Infrastructure.Devices;
using ControlHome.Infrastructure.Persistence;
using ControlHome.Infrastructure.Tests.Devices.Runtime;
using ControlHome.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static ControlHome.Infrastructure.Tests.Devices.DeviceDatabase;

namespace ControlHome.Infrastructure.Tests.Devices;

/// <summary>The runtime pieces that talk to SQL Server: the lease, the write-behind store and the inventory merge.</summary>
public sealed class RuntimeDatabaseTests(SqlServerFixture sql)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Only_one_process_holds_the_runtime_lease_until_it_releases_it()
    {
        await using var database = await CreateAsync(sql, Ct);
        var connections = database.Services.GetRequiredService<IDatabaseConnectionProvider>();
        await using var first = new RuntimeLease(connections, NullLogger<RuntimeLease>.Instance);
        await using var second = new RuntimeLease(connections, NullLogger<RuntimeLease>.Instance);

        (await first.TryAcquireAsync(Ct)).ShouldBeTrue();
        (await second.TryAcquireAsync(Ct)).ShouldBeFalse();
        (await first.CheckAsync(Ct)).ShouldBe(LeaseState.Held);
        (await second.CheckAsync(Ct)).ShouldBe(LeaseState.Lost);

        await first.ReleaseAsync();

        (await second.TryAcquireAsync(Ct)).ShouldBeTrue();
        (await first.TryAcquireAsync(Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task The_state_store_merges_current_values_and_device_updates()
    {
        await using var database = await CreateAsync(sql, Ct);
        var integration = Integration();
        var light = Device(integration.Id, "light-1", "Light", CapabilityIds.Switch, CapabilityIds.Level);
        integration.Devices.Add(light);
        await database.WithDbAsync(async db =>
        {
            db.Integrations.Add(integration);
            await db.SaveChangesAsync(Ct);
        });
        var on = Attribute(light, CapabilityIds.Switch, "on").Id;
        var level = Attribute(light, CapabilityIds.Level, "level").Id;
        var store = new SqlStateStore(database.Services.GetRequiredService<IDatabaseConnectionProvider>(), new FakeTimeProvider(Now));
        var longText = new string('x', 300);

        await store.WriteAsync(
            new StateWriteBatch(
                [
                    new PendingAttributeState(on, StateValue.True, Now, Now),
                    new PendingAttributeState(level, StateValue.String(longText), Now, Now),
                    new PendingAttributeState(987_654, StateValue.True, Now, Now), // deleted meanwhile: skipped
                ],
                [new PendingHistoryRow(on, Now, StateValue.True)],
                [new PendingDeviceUpdate(light.Id, Availability.Offline, Now)]),
            Ct);
        await store.WriteAsync(
            new StateWriteBatch(
                [
                    new PendingAttributeState(on, StateValue.False, Now.AddSeconds(5), Now.AddSeconds(5)),
                    new PendingAttributeState(level, StateValue.Number(1), Now.AddSeconds(-5), Now.AddSeconds(-5)), // older: ignored
                ],
                [],
                [new PendingDeviceUpdate(light.Id, null, Now.AddSeconds(5))]),
            Ct);

        await database.WithDbAsync(async db =>
        {
            var states = await db.AttributeStates.AsNoTracking().OrderBy(s => s.AttributeId).ToListAsync(Ct);
            states.Select(s => s.AttributeId).ShouldBe([on, level]);
            states[0].GetValue().ShouldBe(StateValue.False);
            states[0].LastChangedAtUtc.ShouldBe(Now.AddSeconds(5));
            states[1].GetValue().ShouldBe(StateValue.String(longText));
            var device = await db.Devices.AsNoTracking().SingleAsync(d => d.Id == light.Id, Ct);
            device.Availability.ShouldBe(Availability.Offline);
            device.LastSeenAtUtc.ShouldBe(Now.AddSeconds(5));
        });
    }

    [Fact]
    public async Task The_inventory_imports_merges_retires_and_orphans_devices()
    {
        await using var database = await CreateAsync(sql, Ct);
        var integration = Integration();
        await database.WithDbAsync(async db =>
        {
            db.Integrations.Add(integration);
            await db.SaveChangesAsync(Ct);
        });
        var publisher = new RecordingPublisher();
        var options = Options.Create(new DeviceRuntimeOptions { PublishWindow = TimeSpan.FromMilliseconds(1) });
        using var batcher = new RealtimeBatcher(publisher, TimeProvider.System, options, NullLogger<RealtimeBatcher>.Instance);
        var registry = new DeviceRegistry();
        var inventory = new DeviceInventoryService(
            database.Services.GetRequiredService<IServiceScopeFactory>(),
            registry,
            batcher,
            new RuntimeClock(new FakeTimeProvider(Now)),
            NullLogger<DeviceInventoryService>.Instance);
        var discovered = new DiscoveredDevice(
            "plug-1",
            "Vendor plug",
            DeviceClasses.Outlet,
            "Acme",
            "P1",
            "1.0",
            null,
            new Dictionary<string, string> { ["mac"] = "02:00:00:00:00:01" },
            null,
            [new DiscoveredEndpoint("main", null, [CapabilityBinding.Of(CapabilityIds.Switch), CapabilityBinding.Of(CapabilityIds.Power)])]);

        await inventory.UpsertAsync(integration.Id, discovered, Ct);
        var deviceId = await database.WithDbAsync(db => db.Devices.Where(d => d.ExternalId == "plug-1").Select(d => d.Id).SingleAsync(Ct));
        await database.WithDbAsync(async db =>
        {
            var device = await db.Devices.SingleAsync(d => d.Id == deviceId, Ct);
            device.Name = "Coffee maker";
            await db.SaveChangesAsync(Ct);
        });

        // A second sync: new firmware, the power capability gone, energy added.
        await inventory.UpsertAsync(
            integration.Id,
            discovered with
            {
                Name = "Renamed by vendor",
                Firmware = "2.0",
                Endpoints = [new DiscoveredEndpoint("main", null, [CapabilityBinding.Of(CapabilityIds.Switch), CapabilityBinding.Of(CapabilityIds.Energy)])],
            },
            Ct);

        await database.WithDbAsync(async db =>
        {
            var device = await db.Devices
                .Include(d => d.Endpoints).ThenInclude(e => e.Capabilities).ThenInclude(c => c.Attributes)
                .AsNoTracking()
                .SingleAsync(d => d.Id == deviceId, Ct);
            device.Name.ShouldBe("Coffee maker");
            device.Firmware.ShouldBe("2.0");
            var capabilities = device.Endpoints.Single().Capabilities.ToDictionary(c => c.CapabilityId);
            capabilities.Keys.ShouldBe([CapabilityIds.Switch, CapabilityIds.Power, CapabilityIds.Energy], ignoreOrder: true);
            capabilities[CapabilityIds.Power].Retired.ShouldBeTrue();
            capabilities[CapabilityIds.Energy].Attributes.Select(a => a.Key).ShouldBe(["energy"]);
        });
        registry.TryResolve(new AttributeAddress(integration.Id, "plug-1", "main", CapabilityIds.Energy, "energy"), out _).ShouldBeTrue();
        await batcher.FlushAsync();
        publisher.DevicesChanged.ShouldBeGreaterThanOrEqualTo(1);

        await inventory.RemoveAsync(integration.Id, "plug-1", Ct);

        (await database.WithDbAsync(db => db.Devices.AsNoTracking().SingleAsync(d => d.Id == deviceId, Ct))).IsOrphaned.ShouldBeTrue();
    }

    [Fact]
    public async Task The_inventory_rejects_malformed_devices()
    {
        await using var database = await CreateAsync(sql, Ct);
        var publisher = NullRealtimePublisher.Instance;
        using var batcher = new RealtimeBatcher(publisher, TimeProvider.System, Options.Create(new DeviceRuntimeOptions()), NullLogger<RealtimeBatcher>.Instance);
        var inventory = new DeviceInventoryService(
            database.Services.GetRequiredService<IServiceScopeFactory>(),
            new DeviceRegistry(),
            batcher,
            new RuntimeClock(TimeProvider.System),
            NullLogger<DeviceInventoryService>.Instance);
        var device = new DiscoveredDevice("x", "X", null, null, null, null, null, new Dictionary<string, string>(), null, []);

        await Should.ThrowAsync<ArgumentException>(() => inventory.UpsertAsync(Guid.CreateVersion7(), device, Ct));
        await Should.ThrowAsync<ArgumentException>(() => inventory.UpsertAsync(
            Guid.CreateVersion7(),
            device with { Endpoints = [new DiscoveredEndpoint("main", null, [CapabilityBinding.Of("teleporter")])] },
            Ct));
    }
}

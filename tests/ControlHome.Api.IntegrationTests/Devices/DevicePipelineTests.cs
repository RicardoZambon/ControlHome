using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using ControlHome.Application.Dashboard;
using ControlHome.Application.Devices;
using ControlHome.Application.Realtime;
using ControlHome.Application.Runtime;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using ControlHome.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Api.IntegrationTests.Devices;

/// <summary>Simulated connector → state processor → cache, hub and database, through the real host.</summary>
[Collection(DevicesCollection.Name)]
public sealed class DevicePipelineTests(DevicesApiFactory factory) : DevicesTestBase(factory)
{
    [Fact]
    public async Task A_simulated_integration_imports_its_devices_with_live_state()
    {
        var (admin, _, viewer) = await SignInAllAsync();

        var integration = await CreateIntegrationAsync(admin);

        integration.ConnectorKey.ShouldBe("simulated");
        var devices = await WaitForDevicesAsync(viewer, 5);
        devices.Select(d => d.DeviceClass).ShouldBe(
            [DeviceClasses.AirConditioner, DeviceClasses.Door, DeviceClasses.Light, DeviceClasses.Lock, DeviceClasses.Outlet],
            ignoreOrder: true);
        devices.ShouldAllBe(d => d.IntegrationId == integration.Id && !d.IsOrphaned);
        var light = DeviceOfClass(devices, DeviceClasses.Light);
        MainEndpoint(light).Capabilities.Select(c => c.CapabilityId).ShouldBe([CapabilityIds.Level, CapabilityIds.Switch]);
        var on = MainEndpoint(light).Capabilities.Single(c => c.CapabilityId == CapabilityIds.Switch).Attributes.Single();
        on.Value.Kind.ShouldBe(StateValueKind.Bool);
        on.Restored.ShouldBeFalse();

        var states = (await viewer.GetFromJsonAsync<AttributeStateDto[]>("/api/state", Ct))!;
        states.ShouldContain(s => s.AttributeId == on.AttributeId && s.Value == on.Value);
        var detail = (await viewer.GetFromJsonAsync<DeviceDetailDto>($"/api/devices/{light.Id}", Ct))!;
        (detail.ConnectorKey, detail.ExternalId, detail.IntegrationName).ShouldBe(("simulated", "light-1", "Test simulated"));

        await RecordingRealtimePublisher.WaitUntilAsync(
            async () => (await viewer.GetFromJsonAsync<IntegrationDto[]>("/api/integrations", Ct))!.Single().Status == "Ok",
            "the integration reports Ok");
        var listed = (await viewer.GetFromJsonAsync<IntegrationDto[]>("/api/integrations", Ct))!.Single();
        listed.DeviceCount.ShouldBe(5);
        // Both go through the realtime batcher, so they can arrive after the REST reads above.
        await RecordingRealtimePublisher.WaitUntilAsync(
            () => Factory.Realtime.Messages.Any(m => m.Name == HomeHubMessages.DevicesChanged),
            "DevicesChanged is pushed");
        await RecordingRealtimePublisher.WaitUntilAsync(
            () => Factory.Realtime.Deltas.Any(d => d.Delta.AttributeId == on.AttributeId),
            "the light's state is pushed");
        await RecordingRealtimePublisher.WaitUntilAsync(
            () => Factory.Realtime.IntegrationStatuses.Any(s => s.Id == integration.Id && s.Status == "Ok"),
            "IntegrationStatusChanged(Ok) is pushed");
    }

    [Fact]
    public async Task Current_values_are_written_behind_to_the_database()
    {
        var (admin, _, viewer) = await SignInAllAsync();
        await CreateIntegrationAsync(admin);
        var devices = await WaitForDevicesAsync(viewer, 5);
        var attributeIds = devices.SelectMany(d => d.Endpoints).SelectMany(e => e.Capabilities).SelectMany(c => c.Attributes)
            .Where(a => a.Value.Kind != StateValueKind.Null)
            .Select(a => a.AttributeId)
            .ToList();

        await RecordingRealtimePublisher.WaitUntilAsync(
            async () => await QueryDbAsync(db => db.AttributeStates.CountAsync(s => attributeIds.Contains(s.AttributeId), Ct)) == attributeIds.Count,
            "every reported value is persisted");

        var persisted = await QueryDbAsync(db => db.Devices.AsNoTracking().Select(d => d.Availability).ToListAsync(Ct));
        persisted.ShouldAllBe(a => a == Availability.Online);
    }

    [Fact]
    public async Task Device_reads_serve_the_live_availability_not_the_written_behind_copy()
    {
        var (admin, _, viewer) = await SignInAllAsync();
        await CreateIntegrationAsync(admin);
        var light = DeviceOfClass(await WaitForDevicesAsync(viewer, 5), DeviceClasses.Light);
        await RecordingRealtimePublisher.WaitUntilAsync(
            async () => await QueryDbAsync(db => db.Devices.AsNoTracking().CountAsync(d => d.Availability == Availability.Online, Ct)) == 5,
            "the availability is written behind");

        // The database copy lags (up to a flush interval, or a whole outage): what a resyncing client gets must not.
        await UpdateDbAsync(db => db.Devices.Where(d => d.Id == light.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Availability, Availability.Unknown), Ct));

        var listed = (await viewer.GetFromJsonAsync<DeviceSummaryDto[]>("/api/devices", Ct))!.Single(d => d.Id == light.Id);
        listed.Availability.ShouldBe(nameof(Availability.Online));
        listed.LastSeenAtUtc.ShouldNotBeNull();
        (await viewer.GetFromJsonAsync<DeviceDetailDto>($"/api/devices/{light.Id}", Ct))!.Availability.ShouldBe(nameof(Availability.Online));
    }

    [Fact]
    public async Task An_injected_state_flows_to_the_cache_the_hub_and_the_database()
    {
        var (admin, _, viewer) = await SignInAllAsync();
        await CreateIntegrationAsync(admin);
        var light = DeviceOfClass(await WaitForDevicesAsync(viewer, 5), DeviceClasses.Light);
        var level = AttributeId(light, CapabilityIds.Level, "level");
        // Later than anything the Simulated home reports, even after other tests moved the runtime clock forward.
        var observed = DateTimeOffset.UtcNow.AddDays(10);

        using var response = await admin.PostAsJsonAsync(
            string.Create(CultureInfo.InvariantCulture, $"/api/_test/devices/{level}/state"),
            new { value = StateValue.Number(37), observedAtUtc = observed },
            Ct);

        await response.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
        (await StateOfAsync(viewer, level)).ShouldBe(StateValue.Number(37));
        await RecordingRealtimePublisher.WaitUntilAsync(
            () => Factory.Realtime.Deltas.Any(d => d.Delta.AttributeId == level && d.Delta.Value == StateValue.Number(37)),
            "the delta is pushed");
        await RecordingRealtimePublisher.WaitUntilAsync(
            async () => await QueryDbAsync(db => db.AttributeStates.AsNoTracking().SingleOrDefaultAsync(s => s.AttributeId == level, Ct)) is { Num: 37 },
            "the value is persisted");
    }

    [Fact]
    public async Task Injecting_into_an_unknown_attribute_is_404()
    {
        using var client = Factory.CreateApiClient();

        using var response = await client.PostAsJsonAsync("/api/_test/devices/999999/state", new { value = StateValue.True }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_device_reported_offline_in_testing_is_offline_for_reads_the_dashboard_and_the_hub()
    {
        var (admin, _, viewer) = await SignInAllAsync();
        await CreateIntegrationAsync(admin);
        var plug = DeviceOfClass(await WaitForDevicesAsync(viewer, 5), DeviceClasses.Outlet);

        using var response = await admin.PostAsJsonAsync(
            $"/api/_test/devices/{plug.Id}/availability",
            new { availability = "Offline" },
            Ct);

        await response.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
        (await viewer.GetFromJsonAsync<DeviceDetailDto>($"/api/devices/{plug.Id}", Ct))!.Availability.ShouldBe(nameof(Availability.Offline));
        var dashboard = (await viewer.GetFromJsonAsync<DashboardDto>("/api/dashboard", Ct))!;
        dashboard.OfflineDevices.ShouldHaveSingleItem().Id.ShouldBe(plug.Id);
        await RecordingRealtimePublisher.WaitUntilAsync(
            () => Factory.Realtime.Messages.Any(m => m.Payload is AvailabilityChangedDto { Availability: "Offline" } change && change.DeviceId == plug.Id),
            "AvailabilityChanged(Offline) is pushed");

        // Online again: off the dashboard's list.
        using var back = await admin.PostAsJsonAsync($"/api/_test/devices/{plug.Id}/availability", new { availability = "Online" }, Ct);
        await back.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
        (await viewer.GetFromJsonAsync<DashboardDto>("/api/dashboard", Ct))!.OfflineDevices.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reporting_the_availability_of_an_unknown_device_or_an_unknown_value_is_refused()
    {
        var (admin, _, viewer) = await SignInAllAsync();
        await CreateIntegrationAsync(admin);
        var plug = DeviceOfClass(await WaitForDevicesAsync(viewer, 5), DeviceClasses.Outlet);

        using var unknownDevice = await admin.PostAsJsonAsync(
            $"/api/_test/devices/{Guid.NewGuid()}/availability",
            new { availability = "Offline" },
            Ct);
        using var unknownValue = await admin.PostAsJsonAsync(
            $"/api/_test/devices/{plug.Id}/availability",
            new { availability = "Sleeping" },
            Ct);

        unknownDevice.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        unknownValue.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_runtime_clock_can_be_moved_forward_in_testing()
    {
        using var client = Factory.CreateApiClient();

        using var read = await client.PostAsJsonAsync("/api/_test/clock", new { }, Ct);
        var before = (await read.Content.ReadFromJsonAsync<ClockDto>(Ct))!;
        using var moved = await client.PostAsJsonAsync("/api/_test/clock", new { advanceSeconds = 3600 }, Ct);
        var after = (await moved.Content.ReadFromJsonAsync<ClockDto>(Ct))!;

        (after.OffsetSeconds - before.OffsetSeconds).ShouldBe(3600, tolerance: 0.001);
        after.NowUtc.ShouldBeGreaterThan(before.NowUtc.AddMinutes(59));
    }

    [Fact]
    public async Task Deleting_an_integration_removes_its_devices_and_their_state()
    {
        var (admin, _, viewer) = await SignInAllAsync();
        var integration = await CreateIntegrationAsync(admin);
        await WaitForDevicesAsync(viewer, 5);

        using var response = await admin.DeleteAsync($"/api/integrations/{integration.Id}", Ct);

        await response.ShouldHaveStatusAsync(HttpStatusCode.NoContent);
        (await viewer.GetFromJsonAsync<DeviceSummaryDto[]>("/api/devices", Ct))!.ShouldBeEmpty();
        (await viewer.GetFromJsonAsync<AttributeStateDto[]>("/api/state", Ct))!.ShouldBeEmpty();
        (await QueryDbAsync(db => db.Devices.CountAsync(Ct))).ShouldBe(0);
        (await AuditEntriesAsync("integrations.deleted")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_system_status_shows_the_active_runtime()
    {
        var (_, _, viewer) = await SignInAllAsync();

        var status = (await viewer.GetFromJsonAsync<SystemStatusDto>("/api/system/status", Ct))!;

        status.RuntimeMode.ShouldBe(nameof(RuntimeMode.Active));
        status.HistoryPaused.ShouldBeFalse();
        status.Version.ShouldNotBeNullOrWhiteSpace();
    }

    private sealed record ClockDto(DateTimeOffset NowUtc, double OffsetSeconds);
}

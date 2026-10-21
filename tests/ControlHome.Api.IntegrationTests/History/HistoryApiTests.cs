using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using ControlHome.Api.IntegrationTests.Devices;
using ControlHome.Application.Auditing;
using ControlHome.Application.History;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using ControlHome.Domain.History;
using ControlHome.Infrastructure.Devices;
using ControlHome.Infrastructure.History;
using ControlHome.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Api.IntegrationTests.History;

/// <summary>History, energy and retention settings through the real host (the tests run the rollups themselves).</summary>
[Collection(DevicesCollection.Name)]
public sealed class HistoryApiTests(DevicesApiFactory factory) : DevicesTestBase(factory)
{
    private HistoryRollup Rollup => Factory.Services.GetRequiredService<HistoryRollup>();

    private DateTimeOffset RuntimeNow => Factory.Services.GetRequiredService<RuntimeClock>().Time.GetUtcNow();

    [Fact]
    public async Task Auto_resolution_serves_raw_changes_then_five_minute_then_hourly_statistics_as_the_range_grows()
    {
        var (admin, _, viewer) = await SignInAllAsync();
        await CreateIntegrationAsync(admin);
        var plug = DeviceOfClass(await WaitForDevicesAsync(viewer, 5), DeviceClasses.Outlet);
        var power = AttributeId(plug, CapabilityIds.Power, "power");
        var on = AttributeId(plug, CapabilityIds.Switch, "on");
        await Factory.Services.GetRequiredService<StateProcessor>().WhenIdleAsync(TimeSpan.FromSeconds(10));
        (await Factory.Services.GetRequiredService<StateWriteBehind>().FlushAsync(Ct)).ShouldBeTrue(); // the live values' history

        // Eight days of readings every 10 minutes, ending a day before the live ones.
        var start = RollupMath.Floor(RuntimeNow - TimeSpan.FromDays(9), TimeSpan.FromHours(1));
        await WriteHistoryAsync(power, Enumerable.Range(0, 8 * 144).Select(i => (start.AddMinutes(i * 10), (double)(i % 50))));
        await Rollup.RunOnceAsync(Ct);
        var day = start.AddDays(1);

        var raw = await SeriesAsync(viewer, power, day, day.AddHours(5));
        (raw.AttributeId, raw.Resolution, raw.Unit).ShouldBe((power, "raw", Units.Watt));
        raw.Points.Count.ShouldBe(30);
        raw.Points[0].ShouldBe(new HistoryPointDto(day, 144 % 50, null, null, null));
        raw.Points.Select(p => p.T).ShouldBeInOrder();

        var fiveMinutes = await SeriesAsync(viewer, power, day, day.AddDays(3));
        fiveMinutes.Resolution.ShouldBe("5m");
        fiveMinutes.Points.Count.ShouldBe(3 * 288);
        fiveMinutes.Points.ShouldAllBe(p => p.V != null && p.Min <= p.V && p.V <= p.Max);

        var hourly = await SeriesAsync(viewer, power, start, start.AddDays(8));
        hourly.Resolution.ShouldBe("1h");
        hourly.Points.Count.ShouldBe(8 * 24);
        hourly.Points[0].T.ShouldBe(start);
        hourly.Points[0].V!.Value.ShouldBe(Enumerable.Range(0, 6).Average(), 1e-9); // 0..5, ten minutes each

        // The value in effect at `from` opens a raw series; an explicit resolution is served as asked.
        var shifted = await SeriesAsync(viewer, power, day.AddMinutes(5), day.AddHours(1));
        shifted.Points[0].ShouldBe(new HistoryPointDto(day.AddMinutes(5), 144 % 50, null, null, null));
        (await SeriesAsync(viewer, power, day, day.AddDays(3), "raw")).Points.Count.ShouldBe(3 * 144);
        (await SeriesAsync(viewer, power, day, day.AddHours(2), "1h")).Points.Select(p => p.T).ShouldBe([day, day.AddHours(1)]);

        // A state (on/off) has no statistics: raw at any range.
        var states = await SeriesAsync(viewer, on, RuntimeNow.AddDays(-3), RuntimeNow.AddMinutes(1));
        (states.Resolution, states.Unit).ShouldBe(("raw", (string?)null));
        states.Points.ShouldNotBeEmpty();
        states.Points.Last().V.ShouldBe(1); // the Simulated plug is on

        // Raw history and 5-minute statistics are purged after their retention: auto never picks a resolution whose rows
        // may be gone for the range (it would chart an empty or cut series while hourly statistics hold it).
        using (var put = await admin.PutAsJsonAsync("/api/settings/retention", new UpdateRetentionSettingsRequest(2, 5, 30, 365), Ct))
        {
            await put.ShouldHaveStatusAsync(HttpStatusCode.OK);
        }

        var end = start.AddDays(8); // the last reading is 10 minutes before
        var recentRaw = await SeriesAsync(viewer, power, end.AddHours(-5), end);
        (recentRaw.Resolution, recentRaw.Points.Count).ShouldBe(("raw", 30));
        (await SeriesAsync(viewer, power, day, day.AddHours(5))).Resolution.ShouldBe("1h");
        (await SeriesAsync(viewer, power, end.AddDays(-3), end)).Resolution.ShouldBe("5m");
        var pastShortTerm = await SeriesAsync(viewer, power, day, day.AddDays(3));
        (pastShortTerm.Resolution, pastShortTerm.Points.Count).ShouldBe(("1h", 3 * 24));
        (await SeriesAsync(viewer, power, day, day.AddHours(5), "raw")).Resolution.ShouldBe("raw"); // asked for: served as is
    }

    [Fact]
    public async Task Hourly_energy_is_capped_per_request()
    {
        var (_, _, viewer) = await SignInAllAsync();
        var start = RollupMath.Floor(RuntimeNow - TimeSpan.FromDays(300), TimeSpan.FromHours(1));

        // Three endpoints with 7 000 hours each: 21 000 rows, more than one request returns.
        await QueryDbAsync(db => db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO [ch].[EnergyHourly] (EndpointId, HourStartUtc, Wh, Source, Estimated)
            SELECT e.Id, DATEADD(hour, h.n, @start), 1, 'counter', 0
            FROM (VALUES (NEWID()), (NEWID()), (NEWID())) AS e(Id)
            CROSS JOIN (SELECT TOP (7000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
                        FROM sys.all_columns AS a CROSS JOIN sys.all_columns AS b) AS h;
            """,
            [new SqlParameter("@start", start.UtcDateTime)],
            Ct));

        await ProblemAsync(viewer, EnergyUrl(start, start.AddDays(300), null), HttpStatusCode.BadRequest, "energy.too_many_rows");
        var narrower = (await viewer.GetFromJsonAsync<EnergyHourlyDto[]>(EnergyUrl(start, start.AddDays(100), null), Ct))!;
        narrower.Length.ShouldBe(3 * 100 * 24);
        HistoryQueryService.MaxEnergyRows.ShouldBeGreaterThan(366 * 24); // a year of one endpoint always fits
    }

    [Fact]
    public async Task A_power_only_simulated_plug_gets_hourly_energy_integrated_from_its_power()
    {
        var (admin, _, viewer) = await SignInAllAsync();
        var spy = Factory.Services.GetRequiredService<PowerOnlySpy>();
        spy.Clear();
        await CreateIntegrationAsync(admin, PowerOnlyConnectorFactory.Key, """{"preset":"basic"}""");
        var plug = DeviceOfClass(await WaitForDevicesAsync(viewer, 5), DeviceClasses.Outlet);
        MainEndpoint(plug).Capabilities.Select(c => c.CapabilityId).ShouldNotContain(CapabilityIds.Energy);
        var endpoint = MainEndpoint(plug).Id;
        var clock = Factory.Services.GetRequiredService<AdjustableTimeProvider>();

        // Jump to just after the next hour, let the plug tick in real time (every 5 s), jump to the hour after, then past it.
        var hour = RollupMath.Floor(clock.GetUtcNow(), TimeSpan.FromHours(1)).AddHours(1);
        await JumpToAsync(clock, hour.AddSeconds(10));
        await RecordingRealtimePublisher.WaitUntilAsync(() => spy.PowerReadingsSince(hour) >= 2, "the plug ticked in real time", timeoutSeconds: 30);
        await JumpToAsync(clock, hour.AddHours(1).AddSeconds(10));
        await RecordingRealtimePublisher.WaitUntilAsync(() => spy.PowerReadingsSince(hour.AddHours(1)) >= 1, "the plug ticked after the hour");
        await JumpToAsync(clock, hour.AddHours(1).AddMinutes(3));
        await Factory.Services.GetRequiredService<StateProcessor>().WhenIdleAsync(TimeSpan.FromSeconds(10));
        (await Factory.Services.GetRequiredService<StateWriteBehind>().FlushAsync(Ct)).ShouldBeTrue();

        await Rollup.RunOnceAsync(Ct);

        var energy = (await viewer.GetFromJsonAsync<EnergyHourlyDto[]>(EnergyUrl(hour, hour.AddHours(1), endpoint), Ct))!;
        var row = energy.ShouldHaveSingleItem();
        (row.EndpointId, row.HourStartUtc, row.Source, row.Estimated).ShouldBe((endpoint, hour, "integratedPower", true));

        // The plug's own meter (which the connector hid) integrates the power in effect: the estimate matches it closely.
        var metered = spy.EnergyAt(hour.AddHours(1)) - spy.EnergyAt(hour);
        metered.ShouldBeGreaterThan(5);
        row.Wh.ShouldBe(metered, 0.5);

        var all = (await viewer.GetFromJsonAsync<EnergyHourlyDto[]>(EnergyUrl(hour.AddHours(-2), hour.AddHours(1), null), Ct))!;
        all.ShouldContain(e => e.EndpointId == endpoint && e.HourStartUtc == hour);
        all.Select(e => e.HourStartUtc).ShouldBeInOrder();
    }

    [Fact]
    public async Task History_requires_sign_in_a_known_attribute_and_a_sensible_range()
    {
        var (_, _, viewer) = await SignInAllAsync();
        var to = RuntimeNow;

        using (var anonymous = Factory.CreateApiClient())
        using (var response = await anonymous.GetAsync(HistoryUrl(1, to.AddHours(-1), to, null), Ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        await ProblemAsync(viewer, HistoryUrl(999_999, to.AddHours(-1), to, null), HttpStatusCode.NotFound, "attributes.not_found");
        await ProblemAsync(viewer, HistoryUrl(1, to.AddHours(-1), to, "10m"), HttpStatusCode.BadRequest, "history.invalid_resolution");
        await ProblemAsync(viewer, HistoryUrl(1, to, to, null), HttpStatusCode.BadRequest, "history.invalid_range");
        await ProblemAsync(viewer, HistoryUrl(1, to.AddDays(-400), to, null), HttpStatusCode.BadRequest, "history.range_too_large");
        await ProblemAsync(viewer, EnergyUrl(to.AddHours(-1), to, Guid.CreateVersion7()), HttpStatusCode.NotFound, "endpoints.not_found");
        await ProblemAsync(viewer, EnergyUrl(to, to.AddHours(-1), null), HttpStatusCode.BadRequest, "history.invalid_range");
        (await viewer.GetFromJsonAsync<EnergyHourlyDto[]>("/api/energy/hourly", Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Admins_read_and_change_how_long_history_is_kept()
    {
        var (admin, member, viewer) = await SignInAllAsync();

        (await admin.GetFromJsonAsync<RetentionSettingsDto>("/api/settings/retention", Ct)).ShouldBe(new RetentionSettingsDto(10, 10, 30, 365));

        using (var response = await admin.PutAsJsonAsync("/api/settings/retention", new UpdateRetentionSettingsRequest(5, 7, 20, 400), Ct))
        {
            await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
            (await response.Content.ReadFromJsonAsync<RetentionSettingsDto>(Ct)).ShouldBe(new RetentionSettingsDto(5, 7, 20, 400));
        }

        (await admin.GetFromJsonAsync<RetentionSettingsDto>("/api/settings/retention", Ct)).ShouldBe(new RetentionSettingsDto(5, 7, 20, 400));
        (await AuditEntriesAsync(AuditActions.RetentionSettingsUpdated)).ShouldHaveSingleItem().DetailsJson
            .ShouldBe("""{"fields":["stateLogDays","shortTermStatsDays","deviceEventDays","auditLogDays"]}""");

        using (var invalid = await admin.PutAsJsonAsync("/api/settings/retention", new UpdateRetentionSettingsRequest(0, 366, 0, 10), Ct))
        {
            await invalid.ShouldBeProblemAsync(HttpStatusCode.BadRequest, "validation.failed");
            var errors = (await invalid.BodyAsync()).GetProperty("errors");
            foreach (var field in new[] { "stateLogDays", "shortTermStatsDays", "deviceEventDays", "auditLogDays" })
            {
                errors.TryGetProperty(field, out _).ShouldBeTrue(field);
            }
        }

        foreach (var client in new[] { member, viewer })
        {
            using var get = await client.GetAsync("/api/settings/retention", Ct);
            get.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            using var put = await client.PutAsJsonAsync("/api/settings/retention", new UpdateRetentionSettingsRequest(5, 7, 20, 400), Ct);
            put.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    private Task WriteHistoryAsync(int attributeId, IEnumerable<(DateTimeOffset At, double Value)> readings) =>
        Factory.Services.GetRequiredService<IStateStore>().WriteAsync(
            new StateWriteBatch([], [.. readings.Select(r => new PendingHistoryRow(attributeId, r.At, StateValue.Number(r.Value)))], []),
            Ct);

    private static async Task JumpToAsync(AdjustableTimeProvider clock, DateTimeOffset target)
    {
        var now = clock.GetUtcNow();
        if (target > now)
        {
            clock.Advance(target - now);
        }

        await Task.Delay(50, Ct);
    }

    private static async Task<HistorySeriesDto> SeriesAsync(HttpClient client, int attributeId, DateTimeOffset from, DateTimeOffset to, string? resolution = null)
    {
        using var response = await client.GetAsync(HistoryUrl(attributeId, from, to, resolution), Ct);
        await response.ShouldHaveStatusAsync(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<HistorySeriesDto>(Ct))!;
    }

    private static async Task ProblemAsync(HttpClient client, string url, HttpStatusCode status, string code)
    {
        using var response = await client.GetAsync(url, Ct);
        await response.ShouldBeProblemAsync(status, code);
    }

    private static string HistoryUrl(int attributeId, DateTimeOffset from, DateTimeOffset to, string? resolution) =>
        string.Create(CultureInfo.InvariantCulture, $"/api/attributes/{attributeId}/history?from={Time(from)}&to={Time(to)}")
        + (resolution is null ? string.Empty : "&resolution=" + Uri.EscapeDataString(resolution));

    private static string EnergyUrl(DateTimeOffset from, DateTimeOffset to, Guid? endpointId) =>
        $"/api/energy/hourly?from={Time(from)}&to={Time(to)}" + (endpointId is { } id ? $"&endpointId={id}" : string.Empty);

    private static string Time(DateTimeOffset value) => Uri.EscapeDataString(value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
}

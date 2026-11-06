using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.History;
using ControlHome.Infrastructure.History;
using ControlHome.TestSupport;
using Microsoft.EntityFrameworkCore;
using static ControlHome.Infrastructure.Tests.History.HistoryHarness;

namespace ControlHome.Infrastructure.Tests.History;

/// <summary>
/// Vendor energy windows into <c>EnergyHourly</c> against SQL Server: spread over their hours (estimated when longer than an
/// hour), idempotent, never on an endpoint whose energy comes from a counter or a user's other choice, and left alone by
/// the rollup afterwards.
/// </summary>
public sealed class VendorEnergyWriterTests(SqlServerFixture sql)
{
    /// <summary>Midnight of 2026-10-11 in São Paulo (UTC−3); <see cref="T0"/> is 10:00 UTC the next day.</summary>
    private static readonly DateTimeOffset _day = new(2026, 10, 11, 3, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EnergyWindowReport Window(string device, DateTimeOffset start, DateTimeOffset end, double wh, string endpoint = "main") =>
        new(device, endpoint, start, end, wh);

    [Fact]
    public async Task A_day_is_spread_over_its_hours_as_estimated_vendor_energy_and_reporting_it_again_changes_nothing()
    {
        await using var history = await CreateAsync(sql, Ct);
        var sensor = MainEndpoint(history.Sensor);

        var result = await history.EnergyWindows.WriteAsync(history.Sensor.IntegrationId, [Window("sensor-1", _day, _day.AddDays(1), 2400)], Ct);

        result.ShouldBe(new EnergyWindowWriteResult(1, 24, 0, 0));
        var rows = await history.EnergyAsync(sensor);
        rows.Count.ShouldBe(24);
        rows.ShouldAllBe(r => r.Wh == 100 && r.Source == EnergySource.VendorWindow && r.Estimated);
        rows[0].HourStartUtc.ShouldBe(_day);
        var selection = await history.Database.WithDbAsync(db => db.EnergySourceSelections.AsNoTracking().SingleAsync(s => s.EndpointId == sensor, Ct));
        (selection.Source, selection.AttributeId, selection.IsUserOverride).ShouldBe((EnergySource.VendorWindow, null, false));

        await history.EnergyWindows.WriteAsync(history.Sensor.IntegrationId, [Window("SENSOR-1", _day, _day.AddDays(1), 2400)], Ct);

        (await history.EnergyAsync(sensor)).Select(r => (r.HourStartUtc, r.Wh, r.Estimated)).ShouldBe(rows.Select(r => (r.HourStartUtc, r.Wh, r.Estimated)));
    }

    [Fact]
    public async Task Days_and_their_month_fill_the_month_without_counting_a_day_twice()
    {
        await using var history = await CreateAsync(sql, Ct);
        var sensor = MainEndpoint(history.Sensor);
        var id = history.Sensor.IntegrationId;

        // A month window of two days; only the first day has daily data. A row inside the window is replaced, one before it
        // is left alone.
        var before = _day.AddDays(-1).AddHours(-1);
        await history.Database.WithDbAsync(async db =>
        {
            db.EnergyHourly.Add(new EnergyHourly { EndpointId = sensor, HourStartUtc = before, Wh = 7, Source = EnergySource.VendorWindow });
            db.EnergyHourly.Add(new EnergyHourly { EndpointId = sensor, HourStartUtc = _day.AddHours(-1), Wh = 7, Source = EnergySource.VendorWindow });
            await db.SaveChangesAsync(Ct);
        });
        await history.EnergyWindows.WriteAsync(
            id,
            [Window("sensor-1", _day.AddDays(-1), _day.AddDays(1), 720), Window("sensor-1", _day.AddDays(-1), _day, 240)],
            Ct);

        var rows = await history.EnergyAsync(sensor);
        rows.Count.ShouldBe(49);
        rows[0].ShouldSatisfyAllConditions(r => r.HourStartUtc.ShouldBe(before), r => r.Wh.ShouldBe(7));
        rows.Where(r => r.HourStartUtc >= _day.AddDays(-1) && r.HourStartUtc < _day).ShouldAllBe(r => r.Wh == 10);
        rows.Where(r => r.HourStartUtc >= _day).Sum(r => r.Wh).ShouldBe(480, 1e-9);
        rows.Skip(1).Sum(r => r.Wh).ShouldBe(720, 1e-9);
    }

    [Fact]
    public async Task An_endpoint_with_an_energy_counter_or_a_users_other_choice_is_skipped()
    {
        await using var history = await CreateAsync(sql, Ct);
        var plug = MainEndpoint(history.Plug);
        var sensor = MainEndpoint(history.Sensor);
        await history.Database.WithDbAsync(async db =>
        {
            db.EnergySourceSelections.Add(new EnergySourceSelection
            {
                EndpointId = sensor,
                Source = EnergySource.IntegratedPower,
                IsUserOverride = true,
                UpdatedAtUtc = T0,
            });
            await db.SaveChangesAsync(Ct);
        });

        var result = await history.EnergyWindows.WriteAsync(
            history.Plug.IntegrationId,
            [Window("plug-1", _day, _day.AddDays(1), 1000), Window("sensor-1", _day, _day.AddDays(1), 1000)],
            Ct);

        result.ShouldBe(new EnergyWindowWriteResult(0, 0, 2, 0));
        (await history.EnergyAsync(plug)).ShouldBeEmpty();
        (await history.EnergyAsync(sensor)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Vendor_windows_replace_an_automatic_integrated_power_choice_and_the_rollup_keeps_them()
    {
        await using var history = await CreateAsync(sql, Ct);
        var powerOnly = MainEndpoint(history.PowerOnly);
        await history.WriteAsync(history.PowerOnlyPower, (T0, 50));
        await history.RollUpAtAsync(T0.AddHours(2).AddMinutes(2));
        (await history.EnergyAsync(powerOnly)).Select(r => (r.HourStartUtc, r.Source)).ShouldBe(
            [(T0, EnergySource.IntegratedPower), (T0.AddHours(1), EnergySource.IntegratedPower)]);

        // The São Paulo day around T0 (03:00 UTC to 03:00 UTC): its windows replace the integrated hours inside it.
        var day = T0.AddHours(-7);
        history.Time.SetUtcNow(T0.AddDays(1));
        await history.EnergyWindows.WriteAsync(history.PowerOnly.IntegrationId, [Window("plug-2", day, day.AddDays(1), 480)], Ct);
        await history.RollUpAtAsync(T0.AddDays(1).AddHours(1));

        var rows = await history.EnergyAsync(powerOnly);
        rows.Count.ShouldBe(24);
        rows[0].HourStartUtc.ShouldBe(day);
        rows.ShouldAllBe(r => r.Source == EnergySource.VendorWindow && r.Wh == 20 && r.Estimated);
        (await history.Database.WithDbAsync(db => db.EnergySourceSelections.AsNoTracking().SingleAsync(s => s.EndpointId == powerOnly, Ct)))
            .Source.ShouldBe(EnergySource.VendorWindow);
    }

    [Fact]
    public async Task Hourly_windows_are_measured_and_unknown_invalid_or_future_windows_are_ignored()
    {
        await using var history = await CreateAsync(sql, Ct);
        var sensor = MainEndpoint(history.Sensor);

        var result = await history.EnergyWindows.WriteAsync(
            history.Sensor.IntegrationId,
            [
                Window("sensor-1", _day, _day.AddHours(1), 42),
                Window("nope", _day, _day.AddHours(1), 1),
                Window("sensor-1", _day, _day.AddHours(1), 1, endpoint: "other"),
                Window("sensor-1", _day, _day.AddHours(1), -1),
                Window("sensor-1", T0, T0.AddHours(1), 1),
            ],
            Ct);

        result.ShouldBe(new EnergyWindowWriteResult(1, 1, 0, 4));
        (await history.EnergyAsync(sensor)).Select(r => (r.HourStartUtc, r.Wh, r.Estimated)).ShouldBe([(_day, 42d, false)]);
    }
}

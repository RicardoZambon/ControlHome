using ControlHome.Domain.History;
using ControlHome.Infrastructure.History;
using ControlHome.TestSupport;
using Microsoft.EntityFrameworkCore;
using static ControlHome.Infrastructure.Tests.History.HistoryHarness;

namespace ControlHome.Infrastructure.Tests.History;

/// <summary>The rollups against SQL Server, on a fake runtime clock.</summary>
public sealed class HistoryRollupTests(SqlServerFixture sql)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset At(double minutes) => T0.AddMinutes(minutes);

    [Fact]
    public async Task Measurements_get_time_weighted_means_with_the_previous_value_carried_into_each_bucket()
    {
        await using var history = await CreateAsync(sql, Ct);
        await history.WriteAsync(history.PlugPower, (At(-3), 100), (At(1), 200), (At(7), 0));

        var result = await history.RollUpAtAsync(At(70));

        var shortTerm = await history.ShortTermAsync(history.PlugPower);
        shortTerm.First().BucketStartUtc.ShouldBe(At(-5)); // the watermark starts at the first reading's bucket
        var byStart = shortTerm.ToDictionary(s => s.BucketStartUtc);
        (byStart[At(-5)].Mean, byStart[At(-5)].Min, byStart[At(-5)].Max).ShouldBe((100, 100, 100));
        byStart[At(0)].Mean!.Value.ShouldBe(((100 * 1) + (200 * 4)) / 5d, 1e-9);
        (byStart[At(0)].Min, byStart[At(0)].Max).ShouldBe((100, 200));
        byStart[At(5)].Mean!.Value.ShouldBe(((200 * 2) + (0 * 3)) / 5d, 1e-9);
        byStart[At(60)].Mean.ShouldBe(0); // carried, no reading since minute 7
        shortTerm.Last().BucketStartUtc.ShouldBe(At(60)); // [65, 70) is not settled yet
        shortTerm.ShouldAllBe(s => s.Sum == null && s.LastCounter == null);

        var hourly = await history.HourlyAsync(history.PlugPower);
        hourly.Select(h => h.BucketStartUtc).ShouldBe([At(-60), At(0)]);
        hourly[0].Mean.ShouldBe(100);
        hourly[1].Mean!.Value.ShouldBe((180 + 80) / 12d, 1e-9); // the mean of the twelve 5-minute means
        (hourly[1].Min, hourly[1].Max).ShouldBe((0, 200));

        (result.ShortTermWatermark, result.HourlyWatermark).ShouldBe((At(65), At(60)));
        (await history.WatermarkAsync(RollupProgress.ShortTerm)).ShouldBe(At(65));
    }

    [Fact]
    public async Task Energy_counters_add_up_increases_and_detect_a_reset()
    {
        await using var history = await CreateAsync(sql, Ct);
        await history.WriteAsync(history.PlugEnergy, (At(1), 1000), (At(2), 1010), (At(6), 5), (At(8), 25), (At(9), 24.5));

        await history.RollUpAtAsync(At(122));

        var shortTerm = await history.ShortTermAsync(history.PlugEnergy);
        shortTerm[0].BucketStartUtc.ShouldBe(At(0));
        (shortTerm[0].Sum, shortTerm[0].LastCounter).ShouldBe((10, 1010)); // the first reading only sets the baseline
        (shortTerm[1].Sum, shortTerm[1].LastCounter).ShouldBe((25, 25));   // reset to 5 counts 5, then +20; the dip to 24.5 is noise
        shortTerm.Skip(2).ShouldAllBe(s => s.Sum == 0 && s.LastCounter == 25);
        shortTerm.ShouldAllBe(s => s.Mean == null);

        var hourly = await history.HourlyAsync(history.PlugEnergy);
        (hourly[0].BucketStartUtc, hourly[0].Sum, hourly[0].LastCounter).ShouldBe((At(0), 35, 25));
        (hourly[1].Sum, hourly[1].LastCounter).ShouldBe((0, 25));

        // The plug has an energy meter: its energy comes from the counter, measured (not estimated).
        var energy = await history.EnergyAsync(MainEndpoint(history.Plug));
        energy.Select(e => (e.HourStartUtc, e.Wh, e.Source, e.Estimated)).ShouldBe(
            [(At(0), 35, EnergySource.Counter, false), (At(60), 0, EnergySource.Counter, false)]);
    }

    [Fact]
    public async Task A_power_only_device_gets_its_energy_integrated_and_flagged_as_estimated()
    {
        await using var history = await CreateAsync(sql, Ct);

        // Off since before the hour; on at 600 W, then 1.2 kW 30 s later (close readings: trapezoid); off after 29.5 min.
        await history.WriteAsync(history.PowerOnlyPower, (At(-10), 0), (At(10), 600), (At(10.5), 1200), (At(40), 0));

        await history.RollUpAtAsync(At(122));

        var energy = await history.EnergyAsync(MainEndpoint(history.PowerOnly));
        energy.Select(e => e.HourStartUtc).ShouldBe([At(-60), At(0), At(60)]);
        energy.ShouldAllBe(e => e.Source == EnergySource.IntegratedPower && e.Estimated);
        energy[0].Wh.ShouldBe(0);
        energy[1].Wh.ShouldBe((900 * 0.5 / 60) + (1200 * 29.5 / 60), 1e-6);
        energy[2].Wh.ShouldBe(0);

        var selection = await history.Database.WithDbAsync(db => db.EnergySourceSelections.AsNoTracking()
            .SingleAsync(s => s.EndpointId == MainEndpoint(history.PowerOnly), Ct));
        (selection.Source, selection.AttributeId, selection.IsUserOverride).ShouldBe((EnergySource.IntegratedPower, history.PowerOnlyPower, false));
    }

    [Fact]
    public async Task A_users_energy_source_is_kept_and_used()
    {
        await using var history = await CreateAsync(sql, Ct);
        var plug = MainEndpoint(history.Plug);
        await history.Database.WithDbAsync(async db =>
        {
            db.EnergySourceSelections.Add(new EnergySourceSelection
            {
                EndpointId = plug,
                Source = EnergySource.IntegratedPower,
                AttributeId = history.PlugPower,
                IsUserOverride = true,
                UpdatedAtUtc = T0,
            });
            await db.SaveChangesAsync(Ct);
        });
        await history.WriteAsync(history.PlugPower, (At(0), 100));
        await history.WriteAsync(history.PlugEnergy, (At(0), 10), (At(30), 60));

        await history.RollUpAtAsync(At(62));

        (await history.EnergyAsync(plug)).Select(e => (e.Wh, e.Source)).ShouldBe([(100, EnergySource.IntegratedPower)]);
        (await history.Database.WithDbAsync(db => db.EnergySourceSelections.AsNoTracking().SingleAsync(s => s.EndpointId == plug, Ct)))
            .IsUserOverride.ShouldBeTrue();
    }

    [Fact]
    public async Task After_downtime_the_rollups_catch_up_window_by_window()
    {
        await using var history = await CreateAsync(sql, Ct, o =>
        {
            o.ShortTermBatch = TimeSpan.FromHours(1);
            o.HourlyBatch = TimeSpan.FromHours(6);
            o.EnergyBatch = TimeSpan.FromHours(6);
        });
        await history.WriteAsync(history.Temperature, (At(0), 20));
        var first = await history.RollUpAtAsync(At(16));
        first.ShortTermWatermark.ShouldBe(At(15));

        // Two days down: readings recorded before the outage are all there is when the rollup runs again.
        await history.WriteAsync(history.Temperature, (At(20), 22), (At(24 * 60), 25));
        var catchUp = await history.RollUpAtAsync(At((48 * 60) + 2));

        catchUp.ShortTermWatermark.ShouldBe(At(48 * 60));
        catchUp.ShortTermBatches.ShouldBe(48);   // from minute 15 in windows of at most an hour
        catchUp.HourlyBatches.ShouldBe(8);       // 48 h in 6-hour windows
        catchUp.HourlyWatermark.ShouldBe(At(48 * 60));
        var shortTerm = await history.ShortTermAsync(history.Temperature);
        shortTerm.Count.ShouldBe(48 * 12);
        shortTerm.Select(s => s.BucketStartUtc).ShouldBe(Enumerable.Range(0, 48 * 12).Select(i => At(i * 5)));
        shortTerm.Single(s => s.BucketStartUtc == At(20)).Mean.ShouldBe(22);
        shortTerm.Single(s => s.BucketStartUtc == At(23 * 60)).Mean.ShouldBe(22);   // carried through the outage
        shortTerm.Last().Mean.ShouldBe(25);
        (await history.HourlyAsync(history.Temperature)).Count.ShouldBe(48);
    }

    [Fact]
    public async Task Re_running_from_an_older_watermark_gives_the_same_rows()
    {
        await using var history = await CreateAsync(sql, Ct);
        await history.WriteAsync(history.PlugPower, (At(-3), 100), (At(1), 200), (At(37), 50));
        await history.WriteAsync(history.PlugEnergy, (At(1), 1000), (At(31), 1100));
        await history.WriteAsync(history.PowerOnlyPower, (At(2), 80));
        await history.RollUpAtAsync(At(130));
        var before = await SnapshotAsync(history);

        var again = await history.RollUpAtAsync(At(130));
        again.ShortTermBatches.ShouldBe(0); // nothing new

        await history.Database.WithDbAsync(db => db.RollupProgress.ExecuteDeleteAsync(Ct));
        var rerun = await history.RollUpAtAsync(At(130));

        rerun.ShortTermBatches.ShouldBeGreaterThan(0);
        (await SnapshotAsync(history)).ShouldBe(before);
    }

    [Fact]
    public async Task Rollups_wait_for_history_the_write_behind_still_holds()
    {
        await using var history = await CreateAsync(sql, Ct);
        await history.WriteAsync(history.Temperature, (At(0), 20));
        history.Pending.OldestPendingHistoryUtc = At(12);

        var result = await history.RollUpAtAsync(At(60));

        result.ShortTermWatermark.ShouldBe(At(10)); // the bucket holding the pending row stays open
        history.Pending.OldestPendingHistoryUtc = null;
        (await history.RollUpAtAsync(At(60))).ShortTermWatermark.ShouldBe(At(55));
    }

    [Fact]
    public async Task Nothing_happens_before_anything_is_recorded()
    {
        await using var history = await CreateAsync(sql, Ct);

        var result = await history.RollUpAtAsync(At(60));

        result.ShouldBe(new RollupRunResult(0, 0, 0, null, null, null));
        (await history.WatermarkAsync(RollupProgress.ShortTerm)).ShouldBeNull();
    }

    private static async Task<string> SnapshotAsync(HistoryHarness history) =>
        await history.Database.WithDbAsync(async db =>
        {
            var shortTerm = await db.ShortTermStatistics.AsNoTracking().OrderBy(s => s.AttributeId).ThenBy(s => s.BucketStartUtc).ToListAsync(Ct);
            var hourly = await db.HourlyStatistics.AsNoTracking().OrderBy(s => s.AttributeId).ThenBy(s => s.BucketStartUtc).ToListAsync(Ct);
            var energy = await db.EnergyHourly.AsNoTracking().OrderBy(e => e.EndpointId).ThenBy(e => e.HourStartUtc).ToListAsync(Ct);
            return string.Join(
                '\n',
                shortTerm.Select(s => $"5m {s.AttributeId} {s.BucketStartUtc:O} {s.Mean} {s.Min} {s.Max} {s.Sum} {s.LastCounter}")
                    .Concat(hourly.Select(s => $"1h {s.AttributeId} {s.BucketStartUtc:O} {s.Mean} {s.Min} {s.Max} {s.Sum} {s.LastCounter}"))
                    .Concat(energy.Select(e => $"E {e.EndpointId} {e.HourStartUtc:O} {e.Wh} {e.Source} {e.Estimated}")));
        });
}

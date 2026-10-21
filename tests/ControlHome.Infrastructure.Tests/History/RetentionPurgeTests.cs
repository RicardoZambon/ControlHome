using ControlHome.Domain.Auditing;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.History;
using ControlHome.Infrastructure.Devices;
using ControlHome.TestSupport;
using Microsoft.EntityFrameworkCore;
using static ControlHome.Infrastructure.Tests.History.HistoryHarness;

namespace ControlHome.Infrastructure.Tests.History;

/// <summary>The retention job against SQL Server: limits, watermarks, the carried value and orphans.</summary>
public sealed class RetentionPurgeTests(SqlServerFixture sql)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset Day(double days) => T0.AddDays(days);

    [Fact]
    public async Task Raw_history_is_purged_only_below_both_the_retention_and_the_rollup_watermark()
    {
        await using var history = await CreateAsync(sql, Ct, o =>
        {
            o.PurgeBatchSize = 7;
            o.ShortTermBatch = TimeSpan.FromDays(1);
        });
        var readings = Enumerable.Range(0, 40).Select(i => (Day((i * 0.5) + 0.25), (double?)i)).ToArray(); // every 12 h for 20 days
        await history.WriteAsync(history.Temperature, readings);
        await history.WriteAsync(history.PlugPower, (Day(0), 5)); // one old reading only: it is the value still in effect

        // The rollups ran until day 5 only (e.g. the database was busy): nothing after their watermark may go.
        await history.RollUpAtAsync(Day(5).AddMinutes(2));
        history.Time.SetUtcNow(Day(20));
        await history.Purge.PurgeOnceAsync(Ct);

        var kept = await history.StateLogAsync(history.Temperature);
        kept.First().TsUtc.ShouldBe(Day(4.75));  // the last reading before the watermark (day 5) stays: it is carried into day 5
        kept.Count.ShouldBe(40 - 9);
        (await history.StateLogAsync(history.PlugPower)).ShouldHaveSingleItem();

        // Once rolled up, the 10-day retention applies.
        await history.RollUpAtAsync(Day(20));
        await history.Purge.PurgeOnceAsync(Ct);

        kept = await history.StateLogAsync(history.Temperature);
        kept.First().TsUtc.ShouldBe(Day(9.75));
        kept.Skip(1).ShouldAllBe(r => r.TsUtc >= Day(10));
        kept.Count.ShouldBe(1 + 20);
        (await history.StateLogAsync(history.PlugPower)).ShouldHaveSingleItem().Num.ShouldBe(5);
    }

    [Fact]
    public async Task Short_term_statistics_are_purged_below_the_retention_and_the_hourly_watermark()
    {
        await using var history = await CreateAsync(sql, Ct, o => o.ShortTermBatch = TimeSpan.FromDays(1));
        await history.WriteAsync(history.Temperature, (Day(0), 20));
        await history.RollUpAtAsync(Day(12));
        await history.Database.WithDbAsync(async db =>
        {
            db.RetentionSettings.Add(new RetentionSettings { ShortTermStatsDays = 3, UpdatedAtUtc = T0 });
            await db.SaveChangesAsync(Ct);
        });

        await history.Purge.PurgeOnceAsync(Ct);

        var shortTerm = await history.ShortTermAsync(history.Temperature);
        shortTerm.First().BucketStartUtc.ShouldBe(Day(9));
        (await history.HourlyAsync(history.Temperature)).First().BucketStartUtc.ShouldBe(Day(0)); // hourly statistics stay forever
    }

    [Fact]
    public async Task Events_and_audit_entries_are_purged_after_their_retention()
    {
        await using var history = await CreateAsync(sql, Ct);
        await history.Store.WriteAsync(
            new StateWriteBatch(
                [],
                [
                    new PendingHistoryRow(history.PlugOn, Day(-40), StateValue.String("single")) { IsEvent = true },
                    new PendingHistoryRow(history.PlugOn, Day(-10), StateValue.String("double")) { IsEvent = true },
                ],
                []),
            Ct);
        await history.Database.WithDbAsync(async db =>
        {
            db.AuditLog.AddRange(
                new AuditLogEntry { OccurredAtUtc = Day(-400), ActorType = AuditActorType.System, Action = "test.old" },
                new AuditLogEntry { OccurredAtUtc = Day(-30), ActorType = AuditActorType.System, Action = "test.recent" });
            await db.SaveChangesAsync(Ct);
        });

        var deleted = await history.Purge.PurgeOnceAsync(Ct);

        (await history.Database.WithDbAsync(db => db.DeviceEvents.AsNoTracking().Select(e => e.EventType).ToListAsync(Ct))).ShouldBe(["double"]);
        (await history.Database.WithDbAsync(db => db.AuditLog.AsNoTracking().Select(e => e.Action).ToListAsync(Ct))).ShouldBe(["test.recent"]);
        (deleted["DeviceEvent"], deleted["AuditLog"]).ShouldBe((1, 1));
    }

    [Fact]
    public async Task History_of_deleted_attributes_and_endpoints_is_removed()
    {
        await using var history = await CreateAsync(sql, Ct);
        var goneEndpoint = Guid.CreateVersion7();
        await history.Database.WithDbAsync(async db =>
        {
            db.HourlyStatistics.AddRange(
                new HourlyStatistic { AttributeId = 987_654, BucketStartUtc = Day(-1), Mean = 1 },
                new HourlyStatistic { AttributeId = history.Temperature, BucketStartUtc = Day(-1), Mean = 2 });
            db.EnergyHourly.AddRange(
                new EnergyHourly { EndpointId = goneEndpoint, HourStartUtc = Day(-1), Wh = 1, Source = EnergySource.Counter },
                new EnergyHourly { EndpointId = MainEndpoint(history.Plug), HourStartUtc = Day(-1), Wh = 2, Source = EnergySource.Counter });
            db.EnergySourceSelections.Add(new EnergySourceSelection { EndpointId = goneEndpoint, Source = EnergySource.Counter, UpdatedAtUtc = T0 });
            await db.SaveChangesAsync(Ct);
        });

        await history.Purge.PurgeOnceAsync(Ct);

        await history.Database.WithDbAsync(async db =>
        {
            (await db.HourlyStatistics.Select(s => s.AttributeId).ToListAsync(Ct)).ShouldBe([history.Temperature]);
            (await db.EnergyHourly.Select(e => e.EndpointId).ToListAsync(Ct)).ShouldBe([MainEndpoint(history.Plug)]);
            (await db.EnergySourceSelections.AnyAsync(s => s.EndpointId == goneEndpoint, Ct)).ShouldBeFalse();
        });
    }

    [Fact]
    public async Task Nothing_is_purged_before_the_first_rollup()
    {
        await using var history = await CreateAsync(sql, Ct);
        await history.WriteAsync(history.Temperature, (Day(-100), 1), (Day(-50), 2));

        await history.Purge.PurgeOnceAsync(Ct);

        (await history.StateLogAsync(history.Temperature)).Count.ShouldBe(2);
    }
}

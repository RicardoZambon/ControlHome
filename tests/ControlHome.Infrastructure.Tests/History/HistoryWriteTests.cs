using ControlHome.Domain.Capabilities;
using ControlHome.Infrastructure.Devices;
using ControlHome.TestSupport;
using Microsoft.EntityFrameworkCore;
using static ControlHome.Infrastructure.Tests.History.HistoryHarness;

namespace ControlHome.Infrastructure.Tests.History;

/// <summary>The write-behind's store appending history to <c>StateLog</c> and <c>DeviceEvent</c>.</summary>
public sealed class HistoryWriteTests(SqlServerFixture sql)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task State_changes_go_to_the_state_log_and_events_to_the_event_log_in_one_flush()
    {
        await using var history = await CreateAsync(sql, Ct);
        var at = T0.AddTicks(1_234_567); // 123.4567 ms: stored to the millisecond
        await history.Store.WriteAsync(
            new StateWriteBatch(
                [new PendingAttributeState(history.PlugOn, StateValue.True, at, at)],
                [
                    new PendingHistoryRow(history.PlugOn, at, StateValue.True),
                    new PendingHistoryRow(history.PlugPower, at, StateValue.Number(61.5)),
                    new PendingHistoryRow(history.PlugPower, at.AddSeconds(1), StateValue.Null),
                    new PendingHistoryRow(history.PlugPower, at.AddSeconds(2), StateValue.String(new string('x', 300))),
                    new PendingHistoryRow(987_654, at, StateValue.Number(1)), // deleted meanwhile: skipped
                    new PendingHistoryRow(history.PlugOn, at, StateValue.String("double")) { IsEvent = true, EventPayload = """{"count":2}""" },
                    new PendingHistoryRow(history.PlugOn, at.AddSeconds(1), StateValue.String("single")) { IsEvent = true },
                    new PendingHistoryRow(987_654, at, StateValue.String("single")) { IsEvent = true },
                ],
                []),
            Ct);

        var on = (await history.StateLogAsync(history.PlugOn)).ShouldHaveSingleItem();
        (on.Num, on.Str, on.TsUtc).ShouldBe(((double?)1, (string?)null, T0.AddMilliseconds(123)));
        var power = await history.StateLogAsync(history.PlugPower);
        power.Select(p => p.Num).ShouldBe([61.5, null, null]);
        power[2].Str!.Length.ShouldBe(255);
        (await history.StateLogAsync(987_654)).ShouldBeEmpty();

        var events = await history.Database.WithDbAsync(db => db.DeviceEvents.AsNoTracking().OrderBy(e => e.TsUtc).ToListAsync(Ct));
        events.Select(e => (e.AttributeId, e.EventType)).ShouldBe([(history.PlugOn, "double"), (history.PlugOn, "single")]);
        events[0].Payload!.Replace(" ", string.Empty, StringComparison.Ordinal).ShouldBe("""{"count":2}""");
        events[1].Payload.ShouldBeNull();
    }

    [Fact]
    public async Task A_large_batch_is_written_in_one_statement_per_table()
    {
        await using var history = await CreateAsync(sql, Ct);
        var rows = Enumerable.Range(0, 20_000)
            .Select(i => new PendingHistoryRow(i % 2 == 0 ? history.PlugPower : history.Temperature, T0.AddMilliseconds(i * 10), StateValue.Number(i)))
            .ToList();

        await history.Store.WriteAsync(new StateWriteBatch([], rows, []), Ct);

        (await history.Database.WithDbAsync(db => db.StateLog.CountAsync(Ct))).ShouldBe(20_000);
    }
}

using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.LgThinQ.Energy;

namespace ControlHome.Connectors.Tests.LgThinQ;

/// <summary>The nightly energy run: when it runs, what it asks for, and how LG's answers become energy windows.</summary>
public sealed class LgEnergyTests
{
    private static readonly TimeZoneInfo _saoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    [Fact]
    public void The_run_is_at_a_random_time_between_one_and_six_in_the_morning()
    {
        var random = new Random(7);
        for (var i = 0; i < 500; i++)
        {
            var time = LgEnergy.RandomTimeOfDay(random);
            time.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromHours(1));
            time.ShouldBeLessThan(TimeSpan.FromHours(6));
        }
    }

    [Fact]
    public void The_next_run_is_today_at_that_local_time_or_tomorrow()
    {
        var timeOfDay = new TimeSpan(3, 17, 0);

        // 02:00 local (05:00 UTC) on 2026-10-21 → 03:17 local today = 06:17 UTC.
        LgEnergy.NextRun(new DateTimeOffset(2026, 10, 21, 5, 0, 0, TimeSpan.Zero), _saoPaulo, timeOfDay)
            .ShouldBe(new DateTimeOffset(2026, 10, 21, 6, 17, 0, TimeSpan.Zero));

        // 10:00 local → tomorrow.
        LgEnergy.NextRun(new DateTimeOffset(2026, 10, 21, 13, 0, 0, TimeSpan.Zero), _saoPaulo, timeOfDay)
            .ShouldBe(new DateTimeOffset(2026, 10, 22, 6, 17, 0, TimeSpan.Zero));
    }

    [Fact]
    public void The_run_keeps_its_local_time_on_a_day_whose_midnight_was_skipped_by_daylight_saving()
    {
        // 2018-11-04 in São Paulo began at 01:00 (−02:00): 03:17 local is 05:17 UTC, not three hours after 01:00.
        LgEnergy.NextRun(new DateTimeOffset(2018, 11, 4, 0, 0, 0, TimeSpan.Zero), _saoPaulo, new TimeSpan(3, 17, 0))
            .ShouldBe(new DateTimeOffset(2018, 11, 4, 5, 17, 0, TimeSpan.Zero));

        // The night the clocks went back (2019-02-17, 00:00 −02:00 → 23:00 −03:00 the day before): 03:17 is −03:00.
        LgEnergy.NextRun(new DateTimeOffset(2019, 2, 17, 4, 0, 0, TimeSpan.Zero), _saoPaulo, new TimeSpan(3, 17, 0))
            .ShouldBe(new DateTimeOffset(2019, 2, 17, 6, 17, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_run_asks_for_every_day_since_the_first_of_last_month_and_for_both_months()
    {
        var query = LgEnergy.Query(new DateTimeOffset(2026, 10, 21, 6, 17, 0, TimeSpan.Zero), _saoPaulo);

        query.ShouldBe(new LgEnergyQuery(new DateOnly(2026, 10, 21), new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 20), new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1)));

        // On the first of a month, yesterday is the last day of last month.
        LgEnergy.Query(new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero), _saoPaulo).DailyTo.ShouldBe(new DateOnly(2026, 10, 31));
    }

    [Fact]
    public void Days_and_months_become_windows_between_local_midnights_never_reaching_today()
    {
        var query = LgEnergy.Query(new DateTimeOffset(2026, 10, 21, 6, 17, 0, TimeSpan.Zero), _saoPaulo);
        var daily = LgFixtures.Parse(
            """{"resultCode":"0000","result":{"dataList":[{"usedDate":"20260930","energyUsage":1200},{"usedDate":"20261020","energyUsage":800.5},{"usedDate":"20261021","energyUsage":99},{"usedDate":"bogus","energyUsage":1},{"usedDate":"20261019"}]}}""");
        var monthly = LgFixtures.Parse(
            """{"resultCode":"0000","result":{"dataList":[{"usedDate":"202609","energyUsage":30000},{"usedDate":"202610","energyUsage":15000}]}}""");

        var windows = LgEnergy.Windows("test-ac-0001", "main", "energyUsage", daily, monthly, query, _saoPaulo);

        windows.ShouldBe(
        [
            new EnergyWindowReport("test-ac-0001", "main", Utc(2026, 9, 30, 3), Utc(2026, 10, 1, 3), 1200),
            new EnergyWindowReport("test-ac-0001", "main", Utc(2026, 10, 20, 3), Utc(2026, 10, 21, 3), 800.5),
            new EnergyWindowReport("test-ac-0001", "main", Utc(2026, 9, 1, 3), Utc(2026, 10, 1, 3), 30000),
            new EnergyWindowReport("test-ac-0001", "main", Utc(2026, 10, 1, 3), Utc(2026, 10, 21, 3), 15000), // this month: up to today
        ]);
        windows.Where(w => w.EndUtc - w.StartUtc == TimeSpan.FromDays(1)).ShouldAllBe(w => !w.IsHourly);
    }

    [Fact]
    public void Dates_under_another_field_name_or_format_are_still_found()
    {
        // The SDKs only sum dataList: the date field's name is not documented, so any member holding a date of the range counts.
        var query = LgEnergy.Query(new DateTimeOffset(2026, 10, 21, 6, 17, 0, TimeSpan.Zero), _saoPaulo);
        var daily = LgFixtures.Parse(
            """{"result":{"dataList":[{"useDate":"2026-10-19","energyUsage":700},{"day":20261020,"energyUsage":800},{"timestamp":1760918400000,"energyUsage":5}]}}""");
        var monthly = LgFixtures.Parse("""{"result":{"dataList":[{"period":"2026-09","energyUsage":30000}]}}""");

        var windows = LgEnergy.Windows("d", "main", "energyUsage", daily, monthly, query, _saoPaulo);

        windows.ShouldBe(
        [
            new EnergyWindowReport("d", "main", Utc(2026, 10, 19, 3), Utc(2026, 10, 20, 3), 700),
            new EnergyWindowReport("d", "main", Utc(2026, 10, 20, 3), Utc(2026, 10, 21, 3), 800),
            new EnergyWindowReport("d", "main", Utc(2026, 9, 1, 3), Utc(2026, 10, 1, 3), 30000),
        ]);
        LgEnergy.IsUndated(daily, "energyUsage", LgEnergy.Daily, query.DailyFrom, query.DailyTo).ShouldBeFalse();
    }

    [Fact]
    public void An_answer_whose_entries_carry_no_recognisable_date_is_undated_and_its_total_is_the_sum()
    {
        var query = LgEnergy.Query(new DateTimeOffset(2026, 10, 21, 6, 17, 0, TimeSpan.Zero), _saoPaulo);
        var daily = LgFixtures.Parse("""{"result":{"dataList":[{"energyUsage":100},{"energyUsage":250.5},{"other":1}]}}""");

        LgEnergy.Windows("d", "main", "energyUsage", daily, null, query, _saoPaulo).ShouldBeEmpty();
        LgEnergy.IsUndated(daily, "energyUsage", LgEnergy.Daily, query.DailyFrom, query.DailyTo).ShouldBeTrue();
        LgEnergy.Total(daily, "energyUsage").ShouldBe(350.5);
        LgEnergy.IsUndated(LgFixtures.Parse("""{"result":{"dataList":[]}}"""), "energyUsage", LgEnergy.Daily, query.DailyFrom, query.DailyTo).ShouldBeFalse();
        LgEnergy.IsUndated(null, "energyUsage", LgEnergy.Daily, query.DailyFrom, query.DailyTo).ShouldBeFalse();
        LgEnergy.Total(LgFixtures.Parse("""{"result":{}}"""), "energyUsage").ShouldBeNull();
        LgEnergy.Day("d", "main", new DateOnly(2026, 10, 20), 42, _saoPaulo)
            .ShouldBe(new EnergyWindowReport("d", "main", Utc(2026, 10, 20, 3), Utc(2026, 10, 21, 3), 42));
    }

    [Fact]
    public void A_single_undated_answer_for_a_single_day_is_that_day()
    {
        var query = new LgEnergyQuery(new DateOnly(2026, 10, 21), new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 1));

        var windows = LgEnergy.Windows("d", "main", "energyUsage", LgFixtures.Parse("""{"result":{"dataList":[{"energyUsage":42}]}}"""), null, query, _saoPaulo);

        windows.ShouldBe([new EnergyWindowReport("d", "main", Utc(2026, 10, 20, 3), Utc(2026, 10, 21, 3), 42)]);
    }

    [Fact]
    public void A_midnight_skipped_by_daylight_saving_starts_the_day_at_its_first_valid_time()
    {
        // Brazil's old DST (2018-11-04) started at midnight: 00:00 did not exist, the day began at 01:00 (−02:00).
        var day = LgEnergy.ToUtc(new DateOnly(2018, 11, 4), _saoPaulo);

        day.ShouldBe(new DateTimeOffset(2018, 11, 4, 3, 0, 0, TimeSpan.Zero));
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour) => new(year, month, day, hour, 0, 0, TimeSpan.Zero);
}

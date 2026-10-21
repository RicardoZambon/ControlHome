using ControlHome.Domain.History;

namespace ControlHome.Domain.Tests.History;

/// <summary>
/// <see cref="EnergyWindowMath.Spread"/>: vendor energy windows (an LG day or month, a SmartThings 15-minute delta) become
/// hourly energy rows: measured hours as they are, longer windows spread evenly (estimated), finer windows of the same
/// report winning over coarser ones, and the same report always giving the same rows.
/// </summary>
public sealed class EnergyWindowMathTests
{
    /// <summary>Midnight in São Paulo (UTC−3): 03:00 UTC.</summary>
    private static readonly DateTimeOffset _day = new(2026, 10, 20, 3, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset H(int hours) => _day.AddHours(hours);

    private static EnergyWindow W(DateTimeOffset start, DateTimeOffset end, double wh) => new(start, end, wh);

    [Fact]
    public void A_day_is_spread_evenly_over_its_24_hours_and_flagged_estimated()
    {
        var hours = EnergyWindowMath.Spread([W(H(0), H(24), 2400)]);

        hours.Count.ShouldBe(24);
        hours.Select(h => h.HourStartUtc).ShouldBe(Enumerable.Range(0, 24).Select(H));
        hours.ShouldAllBe(h => h.Wh == 100 && h.Estimated);
        hours.Sum(h => h.Wh).ShouldBe(2400);
    }

    [Fact]
    public void An_hourly_window_is_measured_not_estimated()
    {
        EnergyWindowMath.Spread([W(H(5), H(6), 321.5)]).ShouldBe([new EnergyHour(H(5), 321.5, Estimated: false)]);
    }

    [Fact]
    public void Sub_hour_windows_add_up_within_their_hour_and_straddling_ones_are_split()
    {
        var hours = EnergyWindowMath.Spread(
        [
            W(H(1), H(1).AddMinutes(15), 10),
            W(H(1).AddMinutes(15), H(1).AddMinutes(30), 20),
            W(H(1).AddMinutes(45), H(2).AddMinutes(15), 60), // half in each hour
        ]);

        hours.ShouldBe([new EnergyHour(H(1), 60, false), new EnergyHour(H(2), 30, false)]);
    }

    [Fact]
    public void A_month_spreads_only_what_its_days_leave_over_the_days_without_data()
    {
        // Three days of a "month" window: two days reported daily, the third only through the month total.
        var hours = EnergyWindowMath.Spread(
        [
            W(H(0), H(72), 1000),
            W(H(0), H(24), 240),
            W(H(24), H(48), 480),
        ]);

        hours.Count.ShouldBe(72);
        hours.Where(h => h.HourStartUtc < H(24)).ShouldAllBe(h => h.Wh == 10);
        hours.Where(h => h.HourStartUtc >= H(24) && h.HourStartUtc < H(48)).ShouldAllBe(h => h.Wh == 20);
        hours.Where(h => h.HourStartUtc >= H(48)).Sum(h => h.Wh).ShouldBe(280, tolerance: 1e-9);
        hours.Sum(h => h.Wh).ShouldBe(1000, tolerance: 1e-9);
        hours.ShouldAllBe(h => h.Estimated);
    }

    [Fact]
    public void A_coarser_window_fully_covered_by_finer_ones_adds_nothing()
    {
        var hours = EnergyWindowMath.Spread([W(H(0), H(48), 999), W(H(0), H(24), 240), W(H(24), H(48), 480)]);

        hours.Sum(h => h.Wh).ShouldBe(720);
    }

    [Fact]
    public void A_coarser_window_smaller_than_its_parts_never_goes_negative()
    {
        var hours = EnergyWindowMath.Spread([W(H(0), H(48), 100), W(H(0), H(24), 240)]);

        hours.Where(h => h.HourStartUtc >= H(24)).ShouldAllBe(h => h.Wh == 0);
        hours.Where(h => h.HourStartUtc < H(24)).Sum(h => h.Wh).ShouldBe(240);
    }

    [Fact]
    public void Measured_hours_inside_a_day_win_and_the_rest_of_the_day_is_spread()
    {
        var hours = EnergyWindowMath.Spread([W(H(0), H(24), 1200), W(H(10), H(11), 200)]);

        hours.Single(h => h.HourStartUtc == H(10)).ShouldBe(new EnergyHour(H(10), 200, false));
        hours.Where(h => h.HourStartUtc != H(10)).ShouldAllBe(h => h.Wh == 1000.0 / 23 && h.Estimated);
    }

    [Fact]
    public void The_same_report_always_gives_the_same_rows_whatever_its_order()
    {
        EnergyWindow[] windows = [W(H(0), H(72), 1000), W(H(24), H(48), 480), W(H(0), H(24), 240), W(H(30), H(31), 50)];

        var first = EnergyWindowMath.Spread(windows);
        var again = EnergyWindowMath.Spread([.. windows.Reverse()]);

        again.ShouldBe(first);
    }

    [Fact]
    public void Day_windows_off_the_hour_are_snapped_to_whole_hours_so_neighbours_stay_adjacent()
    {
        // Midnight in India (UTC+05:30) is 18:30 UTC: two consecutive days.
        var midnight = new DateTimeOffset(2026, 10, 19, 18, 30, 0, TimeSpan.Zero);
        var hours = EnergyWindowMath.Spread([W(midnight, midnight.AddDays(1), 240), W(midnight.AddDays(1), midnight.AddDays(2), 480)]);

        hours.Count.ShouldBe(48);
        hours[0].HourStartUtc.ShouldBe(new DateTimeOffset(2026, 10, 19, 19, 0, 0, TimeSpan.Zero));
        hours.Take(24).ShouldAllBe(h => h.Wh == 10);
        hours.Skip(24).ShouldAllBe(h => h.Wh == 20);
    }

    [Fact]
    public void A_short_day_with_a_dst_change_spreads_over_its_23_hours()
    {
        EnergyWindowMath.Spread([W(H(0), H(23), 230)]).ShouldAllBe(h => h.Wh == 10);
    }

    [Fact]
    public void Duplicate_windows_count_once()
    {
        EnergyWindowMath.Spread([W(H(0), H(24), 240), W(H(0), H(24), 240)]).Sum(h => h.Wh).ShouldBe(240);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Invalid_energy_is_refused(double wh)
    {
        Should.Throw<ArgumentException>(() => EnergyWindowMath.Spread([W(H(0), H(1), wh)]));
    }

    [Fact]
    public void An_empty_or_reversed_window_is_refused()
    {
        Should.Throw<ArgumentException>(() => EnergyWindowMath.Spread([W(H(1), H(1), 1)]));
        Should.Throw<ArgumentException>(() => EnergyWindowMath.Spread([W(H(2), H(1), 1)]));
    }

    [Fact]
    public void Validity_checks_name_the_problem()
    {
        EnergyWindowMath.Validate(W(H(0), H(1), 1)).ShouldBeNull();
        EnergyWindowMath.Validate(W(H(1), H(0), 1)).ShouldNotBeNull();
        EnergyWindowMath.Validate(W(H(0), H(0).AddDays(400), 1)).ShouldNotBeNull();
        EnergyWindowMath.Validate(W(H(0), H(1), -0.5)).ShouldNotBeNull();
    }
}

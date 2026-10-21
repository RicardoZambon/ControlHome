using ControlHome.Domain.History;

namespace ControlHome.Domain.Tests.History;

public sealed class RollupMathTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _fiveMinutes = TimeSpan.FromMinutes(5);

    private static DateTimeOffset At(double minutes) => _t0.AddMinutes(minutes);

    private static Reading R(double minutes, double? value) => new(At(minutes), value);

    [Fact]
    public void Floor_aligns_to_the_bucket_in_utc()
    {
        RollupMath.Floor(At(7.5), _fiveMinutes).ShouldBe(At(5));
        RollupMath.Floor(At(5), _fiveMinutes).ShouldBe(At(5));
        RollupMath.Floor(new DateTimeOffset(2026, 10, 12, 7, 59, 0, TimeSpan.FromHours(-3)), TimeSpan.FromHours(1))
            .ShouldBe(new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void The_mean_is_weighted_by_how_long_each_value_lasted()
    {
        // 10 for 1 min, 20 for 4 min: (10 * 1 + 20 * 4) / 5 = 18.
        var buckets = RollupMath.Measurements(null, [R(0, 10), R(1, 20)], At(0), At(5), _fiveMinutes);

        var bucket = buckets.ShouldHaveSingleItem();
        bucket.BucketStartUtc.ShouldBe(At(0));
        bucket.Mean.ShouldBe(18, 1e-9);
        (bucket.Min, bucket.Max).ShouldBe((10, 20));
    }

    [Fact]
    public void The_value_before_the_range_is_carried_into_it()
    {
        // 30 since before the range, 0 from minute 4: (30 * 4 + 0 * 1) / 5 = 24; the next bucket only knows the carried 0.
        var buckets = RollupMath.Measurements(R(-42, 30), [R(4, 0)], At(0), At(10), _fiveMinutes);

        buckets.Count.ShouldBe(2);
        buckets[0].Mean.ShouldBe(24, 1e-9);
        (buckets[0].Min, buckets[0].Max).ShouldBe((0, 30));
        buckets[1].ShouldBe(new MeasurementStatistic(At(5), 0, 0, 0));
    }

    [Fact]
    public void A_value_spanning_several_buckets_counts_in_each()
    {
        var buckets = RollupMath.Measurements(null, [R(2, 8), R(12, 4)], At(0), At(15), _fiveMinutes);

        buckets.Select(b => b.BucketStartUtc).ShouldBe([At(0), At(5), At(10)]);
        buckets[0].Mean.ShouldBe(8);          // only known from minute 2: unknown time does not count
        buckets[1].Mean.ShouldBe(8);
        buckets[2].Mean.ShouldBe(((8 * 2) + (4 * 3)) / 5d, 1e-9);
    }

    [Fact]
    public void Unknown_values_are_left_out_and_buckets_without_any_value_have_no_row()
    {
        // 10 for 2 min, unknown for 3 min, unknown for the whole second bucket, then 6.
        var buckets = RollupMath.Measurements(null, [R(0, 10), R(2, null), R(11, 6)], At(0), At(15), _fiveMinutes);

        buckets.Select(b => b.BucketStartUtc).ShouldBe([At(0), At(10)]);
        buckets[0].Mean.ShouldBe(10);
        buckets[1].ShouldBe(new MeasurementStatistic(At(10), 6, 6, 6));
    }

    [Fact]
    public void Nothing_known_means_no_buckets()
    {
        RollupMath.Measurements(null, [], At(0), At(15), _fiveMinutes).ShouldBeEmpty();
        RollupMath.Measurements(R(-1, null), [], At(0), At(15), _fiveMinutes).ShouldBeEmpty();
    }

    [Fact]
    public void Ranges_must_be_whole_buckets()
    {
        Should.Throw<ArgumentException>(() => RollupMath.Measurements(null, [], At(1), At(5), _fiveMinutes));
        Should.Throw<ArgumentException>(() => RollupMath.Measurements(null, [], At(0), At(6), _fiveMinutes));
    }

    [Fact]
    public void Counters_sum_the_increases_per_bucket_from_the_baseline()
    {
        var buckets = RollupMath.Counters(100, [R(1, 102), R(4, 105), R(7, 111)], At(0), At(15), _fiveMinutes, CounterKind.TotalIncreasing);

        buckets.ShouldBe(
        [
            new CounterStatistic(At(0), 5, 105),
            new CounterStatistic(At(5), 6, 111),
            new CounterStatistic(At(10), 0, 111), // nothing new: the counter is still known
        ]);
    }

    [Fact]
    public void The_first_reading_ever_only_sets_the_baseline()
    {
        var buckets = RollupMath.Counters(null, [R(6, 500), R(8, 503)], At(0), At(10), _fiveMinutes, CounterKind.TotalIncreasing);

        buckets.ShouldBe([new CounterStatistic(At(5), 3, 503)]);
    }

    [Fact]
    public void A_counter_reset_counts_the_new_reading_from_zero()
    {
        // 1000 → 1004 → reset to 2 (the device restarted its counter) → 7.
        var buckets = RollupMath.Counters(1000, [R(1, 1004), R(2, 2), R(3, 7)], At(0), At(5), _fiveMinutes, CounterKind.TotalIncreasing);

        buckets.ShouldBe([new CounterStatistic(At(0), 4 + 2 + 5, 7)]);
    }

    [Fact]
    public void A_small_dip_is_noise_not_a_reset()
    {
        // 1000 → 995 (less than 10 % down) → 1003: only the 3 above the highest reading count.
        var buckets = RollupMath.Counters(1000, [R(1, 995), R(2, 1003)], At(0), At(5), _fiveMinutes, CounterKind.TotalIncreasing);

        buckets.ShouldBe([new CounterStatistic(At(0), 3, 1003)]);
    }

    [Fact]
    public void A_dip_keeps_the_highest_reading_as_the_last_counter()
    {
        var buckets = RollupMath.Counters(1000, [R(1, 995)], At(0), At(5), _fiveMinutes, CounterKind.TotalIncreasing);

        buckets.ShouldBe([new CounterStatistic(At(0), 0, 1000)]);
    }

    [Fact]
    public void A_total_may_go_down()
    {
        var buckets = RollupMath.Counters(50, [R(1, 40), R(2, 45)], At(0), At(5), _fiveMinutes, CounterKind.Total);

        buckets.ShouldBe([new CounterStatistic(At(0), -5, 45)]);
    }

    [Fact]
    public void Unknown_counter_readings_are_skipped()
    {
        var buckets = RollupMath.Counters(10, [R(1, null), R(2, 12)], At(0), At(5), _fiveMinutes, CounterKind.TotalIncreasing);

        buckets.ShouldBe([new CounterStatistic(At(0), 2, 12)]);
    }

    [Fact]
    public void Power_between_close_readings_is_integrated_with_the_trapezoid_rule()
    {
        // 100 W → 200 W over 30 s (linear), then 200 W held: 150 W * 30 s + 200 W * 4.5 min.
        var limit = TimeSpan.FromMinutes(1);
        var buckets = RollupMath.IntegratePower(null, [R(0, 100), R(0.5, 200)], null, At(0), At(5), _fiveMinutes, limit);

        var bucket = buckets.ShouldHaveSingleItem();
        bucket.BucketStartUtc.ShouldBe(At(0));
        bucket.Wh.ShouldBe((150 * 0.5 / 60) + (200 * 4.5 / 60), 1e-9);
    }

    [Fact]
    public void Power_between_distant_readings_is_held_because_history_only_stores_changes()
    {
        // Off (0 W) for 3 h, then on at 1 kW: the 0 W lasted until the change (a plain trapezoid would invent 1.5 kWh).
        var hour = TimeSpan.FromHours(1);
        var buckets = RollupMath.IntegratePower(R(0, 0), [R(180, 1000)], null, At(0), At(240), hour, TimeSpan.FromMinutes(1));

        buckets.Select(b => b.Wh).ShouldBe([0, 0, 0, 1000]);
    }

    [Fact]
    public void Power_is_interpolated_across_bucket_boundaries()
    {
        // 0 W at 4.5 min → 60 W at 5.5 min (linear): 30 W at the boundary.
        var buckets = RollupMath.IntegratePower(null, [R(4.5, 0), R(5.5, 60)], null, At(0), At(10), _fiveMinutes, TimeSpan.FromMinutes(2));

        buckets.Count.ShouldBe(2);
        buckets[0].Wh.ShouldBe(15 * 0.5 / 60, 1e-9);                        // 0 → 30 W over 30 s
        buckets[1].Wh.ShouldBe((45 * 0.5 / 60) + (60 * 4.5 / 60), 1e-9);    // 30 → 60 W over 30 s, then 60 W
    }

    [Fact]
    public void The_carried_and_the_next_reading_shape_the_edges_of_the_range()
    {
        // Carried 100 W at -0.5 min and 200 W at +0.5 min: 150 W at the start of the range; the reading after the range
        // (5.5 min, 0 W) pulls the end down linearly from 200 W at 4.5 min.
        var limit = TimeSpan.FromMinutes(1);
        var buckets = RollupMath.IntegratePower(R(-0.5, 100), [R(0.5, 200), R(4.5, 200)], R(5.5, 0), At(0), At(5), _fiveMinutes, limit);

        var expected = (175 * 0.5 / 60) + (200 * 4 / 60d) + (150 * 0.5 / 60);
        buckets.ShouldHaveSingleItem().Wh.ShouldBe(expected, 1e-9);
    }

    [Fact]
    public void Unknown_power_adds_no_energy_and_no_value_means_no_bucket()
    {
        var buckets = RollupMath.IntegratePower(null, [R(0, 120), R(2.5, null), R(11, 60)], null, At(0), At(15), _fiveMinutes, TimeSpan.FromMinutes(1));

        buckets.Select(b => b.BucketStartUtc).ShouldBe([At(0), At(10)]);
        buckets[0].Wh.ShouldBe(120 * 2.5 / 60, 1e-9);
        buckets[1].Wh.ShouldBe(60 * 4 / 60d, 1e-9);
    }

    [Fact]
    public void Hourly_measurements_combine_the_short_term_buckets()
    {
        var combined = RollupMath.CombineMeasurements(
            At(0),
            [new MeasurementStatistic(At(0), 10, 5, 12), new MeasurementStatistic(At(5), 20, 18, 40)]);

        combined.ShouldBe(new MeasurementStatistic(At(0), 15, 5, 40));
    }

    [Fact]
    public void Hourly_counters_add_the_short_term_sums_and_keep_the_last_counter()
    {
        var combined = RollupMath.CombineCounters(
            At(0),
            [new CounterStatistic(At(5), 2, 104), new CounterStatistic(At(0), 3, 102)]);

        combined.ShouldBe(new CounterStatistic(At(0), 5, 104));
    }
}

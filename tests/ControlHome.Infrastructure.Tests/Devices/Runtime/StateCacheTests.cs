using ControlHome.Domain.Capabilities;
using ControlHome.Infrastructure.Devices;

namespace ControlHome.Infrastructure.Tests.Devices.Runtime;

public sealed class StateCacheTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void First_value_is_a_change()
    {
        var cache = new StateCache();

        var result = cache.Apply(1, StateValue.True, _t0);

        result.Outcome.ShouldBe(StateApplyOutcome.Changed);
        result.Previous.ShouldBeNull();
        cache.TryGet(1, out var state).ShouldBeTrue();
        state.ShouldBe(new CachedState(StateValue.True, _t0, _t0, Restored: false));
    }

    [Fact]
    public void Same_value_with_the_same_time_is_a_duplicate()
    {
        var cache = new StateCache();
        cache.Apply(1, StateValue.Number(21.5), _t0);

        var result = cache.Apply(1, StateValue.Number(21.5), _t0);

        result.Outcome.ShouldBe(StateApplyOutcome.Unchanged);
    }

    [Fact]
    public void Same_value_observed_later_is_unchanged_but_remembers_the_newer_observation()
    {
        var cache = new StateCache();
        cache.Apply(1, StateValue.Number(21.5), _t0);

        var result = cache.Apply(1, StateValue.Number(21.5), _t0.AddSeconds(5));

        result.Outcome.ShouldBe(StateApplyOutcome.Unchanged);
        cache.TryGet(1, out var state).ShouldBeTrue();
        state.ObservedAtUtc.ShouldBe(_t0.AddSeconds(5));
        state.LastChangedAtUtc.ShouldBe(_t0);
    }

    [Fact]
    public void Older_report_is_dropped_as_out_of_order()
    {
        var cache = new StateCache();
        cache.Apply(1, StateValue.Number(2), _t0.AddSeconds(10));

        var result = cache.Apply(1, StateValue.Number(1), _t0);

        result.Outcome.ShouldBe(StateApplyOutcome.OutOfOrder);
        cache.TryGet(1, out var state).ShouldBeTrue();
        state.Value.ShouldBe(StateValue.Number(2));
    }

    [Fact]
    public void A_new_value_moves_last_changed()
    {
        var cache = new StateCache();
        cache.Apply(1, StateValue.False, _t0);

        var result = cache.Apply(1, StateValue.True, _t0.AddSeconds(3));

        result.Outcome.ShouldBe(StateApplyOutcome.Changed);
        result.Previous!.Value.ShouldBe(StateValue.False);
        result.Current.ShouldBe(new CachedState(StateValue.True, _t0.AddSeconds(3), _t0.AddSeconds(3), Restored: false));
    }

    [Fact]
    public void Momentary_events_are_never_deduplicated()
    {
        var cache = new StateCache();
        cache.Apply(1, StateValue.String("single"), _t0, isEvent: true);

        cache.Apply(1, StateValue.String("single"), _t0, isEvent: true).Outcome.ShouldBe(StateApplyOutcome.Changed);
        cache.Apply(1, StateValue.String("single"), _t0.AddSeconds(-1), isEvent: true).Outcome.ShouldBe(StateApplyOutcome.Changed);
    }

    [Fact]
    public void Restored_values_are_confirmed_by_the_first_report_of_the_same_value()
    {
        var cache = new StateCache();
        cache.Load([Row(7, StateValue.True, _t0)]);
        cache.IsHydrated.ShouldBeTrue();
        cache.TryGet(7, out var restored).ShouldBeTrue();
        restored.Restored.ShouldBeTrue();

        var result = cache.Apply(7, StateValue.True, _t0.AddMinutes(1));

        result.Outcome.ShouldBe(StateApplyOutcome.Confirmed);
        result.Current.Restored.ShouldBeFalse();
        result.Current.LastChangedAtUtc.ShouldBe(_t0);
    }

    private static Domain.Devices.AttributeState Row(int attributeId, StateValue value, DateTimeOffset at)
    {
        var row = new Domain.Devices.AttributeState { AttributeId = attributeId };
        row.SetValue(value, at, at);
        return row;
    }
}

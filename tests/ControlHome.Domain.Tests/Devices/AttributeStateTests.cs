using System.Text.Json;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;

namespace ControlHome.Domain.Tests.Devices;

public sealed class AttributeStateTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

    public static TheoryData<StateValue> Values() =>
    [
        StateValue.Null,
        StateValue.True,
        StateValue.False,
        StateValue.Number(23.75),
        StateValue.String("heat"),
        StateValue.String(new string('é', AttributeState.MaxStringLength)),
        StateValue.String(new string('x', AttributeState.MaxStringLength + 1)),
        StateValue.Json(JsonDocument.Parse("""{"zones":[1,2]}""").RootElement),
    ];

    [Theory]
    [MemberData(nameof(Values))]
    public void Values_round_trip_through_the_columns(StateValue value)
    {
        var (kind, num, str, json) = AttributeState.Encode(value);

        AttributeState.Decode(kind, num, str, json).ShouldBe(value);
        kind.ShouldBe(value.Kind);
    }

    [Fact]
    public void Booleans_are_stored_as_numbers_and_long_strings_as_json()
    {
        AttributeState.Encode(StateValue.True).ShouldBe((StateValueKind.Bool, 1d, (string?)null, (string?)null));

        var longText = new string('x', AttributeState.MaxStringLength + 1);
        var (_, _, str, json) = AttributeState.Encode(StateValue.String(longText));
        str.ShouldBeNull();
        JsonSerializer.Deserialize<string>(json!).ShouldBe(longText);
    }

    [Fact]
    public void Last_changed_moves_only_when_the_value_changes()
    {
        var state = new AttributeState { AttributeId = 7 };

        state.SetValue(StateValue.Number(20), _t0, _t0).ShouldBeTrue();
        state.SetValue(StateValue.Number(20), _t0.AddMinutes(1), _t0.AddMinutes(1)).ShouldBeFalse();
        state.LastChangedAtUtc.ShouldBe(_t0);
        state.ObservedAtUtc.ShouldBe(_t0.AddMinutes(1));

        state.SetValue(StateValue.Number(21), _t0.AddMinutes(2), _t0.AddMinutes(3)).ShouldBeTrue();
        state.LastChangedAtUtc.ShouldBe(_t0.AddMinutes(2));
        state.UpdatedAtUtc.ShouldBe(_t0.AddMinutes(3));
        state.GetValue().ShouldBe(StateValue.Number(21));
    }

    [Fact]
    public void The_first_value_counts_as_a_change_even_when_null()
    {
        var state = new AttributeState();

        state.SetValue(StateValue.Null, _t0, _t0).ShouldBeTrue();
        state.LastChangedAtUtc.ShouldBe(_t0);
    }

    [Fact]
    public void Inconsistent_columns_are_reported()
    {
        Should.Throw<InvalidOperationException>(() => AttributeState.Decode(StateValueKind.Number, null, "1", null));
    }
}

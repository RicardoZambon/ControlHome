using ControlHome.Domain.Capabilities;
using ControlHome.Domain.History;

namespace ControlHome.Domain.Tests.History;

public sealed class HistoryClassificationTests
{
    private static AttributeDefinition Catalogue(string capability, string key) => CapabilityCatalog.Find(capability)!.FindAttribute(key)!;

    [Fact]
    public void Recorded_numbers_get_statistics_by_their_state_class()
    {
        HistoryClassification.KindOf(Catalogue(CapabilityIds.Power, "power")).ShouldBe(StatisticKind.Measurement);
        HistoryClassification.KindOf(Catalogue(CapabilityIds.Temperature, "temperature")).ShouldBe(StatisticKind.Measurement);
        HistoryClassification.KindOf(Catalogue(CapabilityIds.Energy, "energy")).ShouldBe(StatisticKind.TotalIncreasing);
        HistoryClassification.KindOf(new AttributeDefinition("net", new NumberSchema(), Units.WattHour, StateClass.Total, AttributeCategory.Primary, true, false))
            .ShouldBe(StatisticKind.Total);
    }

    [Fact]
    public void States_strings_and_unrecorded_values_get_none()
    {
        HistoryClassification.KindOf(Catalogue(CapabilityIds.Switch, "on")).ShouldBe(StatisticKind.None);
        HistoryClassification.KindOf(Catalogue(CapabilityIds.Level, "level")).ShouldBe(StatisticKind.None); // no state class
        HistoryClassification.KindOf(new AttributeDefinition("rssi", new NumberSchema(), null, StateClass.Measurement, AttributeCategory.Diagnostic, false, false))
            .ShouldBe(StatisticKind.None);
        HistoryClassification.KindOf(null).ShouldBe(StatisticKind.None);
    }

    [Fact]
    public void An_energy_counter_wins_over_power()
    {
        var picked = HistoryClassification.PickEnergySource(
            [
                new EnergyCandidate(7, CapabilityIds.Power, Catalogue(CapabilityIds.Power, "power")),
                new EnergyCandidate(8, CapabilityIds.Energy, Catalogue(CapabilityIds.Energy, "energy")),
            ],
            hasVendorWindows: true);

        picked.ShouldBe((EnergySource.Counter, 8));
    }

    [Fact]
    public void Vendor_windows_win_over_power_and_power_is_the_last_resort()
    {
        EnergyCandidate[] powerOnly = [new EnergyCandidate(7, CapabilityIds.Power, Catalogue(CapabilityIds.Power, "power"))];

        HistoryClassification.PickEnergySource(powerOnly, hasVendorWindows: true).ShouldBe((EnergySource.VendorWindow, (int?)null));
        HistoryClassification.PickEnergySource(powerOnly, hasVendorWindows: false).ShouldBe((EnergySource.IntegratedPower, 7));
    }

    [Fact]
    public void Generic_watt_and_watt_hour_attributes_count_but_the_catalogue_ones_come_first()
    {
        var genericPower = new AttributeDefinition("activePower", new NumberSchema(null, null, null, Units.Watt), Units.Watt, StateClass.Measurement, AttributeCategory.Primary, true, false);
        var unrecordedCounter = new AttributeDefinition("total", new NumberSchema(null, null, null, Units.WattHour), Units.WattHour, StateClass.TotalIncreasing, AttributeCategory.Primary, false, false);

        HistoryClassification.PickEnergySource(
                [new EnergyCandidate(3, CapabilityIds.Generic, genericPower), new EnergyCandidate(4, CapabilityIds.Generic, unrecordedCounter)],
                hasVendorWindows: false)
            .ShouldBe((EnergySource.IntegratedPower, 3));
        HistoryClassification.PickEnergySource(
                [new EnergyCandidate(3, CapabilityIds.Generic, genericPower), new EnergyCandidate(9, CapabilityIds.Power, Catalogue(CapabilityIds.Power, "power"))],
                hasVendorWindows: false)
            .ShouldBe((EnergySource.IntegratedPower, 9));
        HistoryClassification.PickEnergySource([], hasVendorWindows: false).ShouldBeNull();
    }

    [Fact]
    public void Energy_source_names_round_trip()
    {
        foreach (var source in Enum.GetValues<EnergySource>())
        {
            EnergySources.Parse(EnergySources.Name(source)).ShouldBe(source);
        }

        EnergySources.Name(EnergySource.IntegratedPower).ShouldBe("integratedPower");
        EnergySources.TryParse("solar", out _).ShouldBeFalse();
    }

    [Fact]
    public void State_log_columns_hold_numbers_booleans_and_cut_strings()
    {
        StateLogEntry.Encode(StateValue.Number(21.5)).ShouldBe(((double?)21.5, (string?)null));
        StateLogEntry.Encode(StateValue.True).ShouldBe(((double?)1, (string?)null));
        StateLogEntry.Encode(StateValue.String("heat")).ShouldBe(((double?)null, "heat"));
        StateLogEntry.Encode(StateValue.String(new string('x', 300))).Str!.Length.ShouldBe(StateLogEntry.MaxStringLength);
        StateLogEntry.Encode(StateValue.Null).ShouldBe(((double?)null, (string?)null));
    }
}

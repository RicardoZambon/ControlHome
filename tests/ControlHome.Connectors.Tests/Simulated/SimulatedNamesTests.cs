using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.Simulated;

namespace ControlHome.Connectors.Tests.Simulated;

/// <summary>The simulated devices are named in the home's language (config <c>language</c>), and so are their rooms.</summary>
public sealed class SimulatedNamesTests
{
    private static IReadOnlyList<DiscoveredDevice> Describe(string config) =>
        new SimulatedConnectorFactory().DescribeDevices(SimulatedHarness.Json(config));

    [Fact]
    public void English_is_the_default()
    {
        var devices = Describe("""{"preset":"basic"}""");

        devices.Select(d => (d.Name, d.SuggestedRoom)).ShouldBe(
        [
            ("Living room light", "Living room"),
            ("Coffee maker plug", "Kitchen"),
            ("Bedroom air conditioner", "Bedroom"),
            ("Front door sensor", "Entrance"),
            ("Front door lock", "Entrance"),
        ]);
    }

    [Fact]
    public void A_portuguese_home_gets_portuguese_names_and_rooms()
    {
        var devices = Describe("""{"preset":"basic","language":"pt-BR"}""");

        devices.Select(d => (d.Name, d.SuggestedRoom)).ShouldBe(
        [
            ("Luz da sala", "Sala"),
            ("Tomada da cafeteira", "Cozinha"),
            ("Ar-condicionado do quarto", "Quarto"),
            ("Sensor da porta", "Entrada"),
            ("Fechadura da porta", "Entrada"),
        ]);
    }

    [Fact]
    public void Every_demo_device_room_and_control_has_a_portuguese_name()
    {
        var english = Describe("""{"preset":"demo","language":"en"}""");
        var portuguese = Describe("""{"preset":"demo","language":"pt-BR"}""");

        portuguese.Select(d => d.ExternalId).ShouldBe(english.Select(d => d.ExternalId));
        foreach (var (en, pt) in english.Zip(portuguese))
        {
            pt.Name.ShouldNotBe(en.Name, $"{en.ExternalId} is named in Portuguese");
            pt.SuggestedRoom.ShouldNotBe(en.SuggestedRoom, $"{en.ExternalId}'s room is named in Portuguese");
            foreach (var (enEndpoint, ptEndpoint) in en.Endpoints.Zip(pt.Endpoints))
            {
                if (enEndpoint.Name is not null)
                {
                    ptEndpoint.Name.ShouldNotBe(enEndpoint.Name, $"{en.ExternalId}/{enEndpoint.Key} is named in Portuguese");
                }
            }
        }

        portuguese.Single(d => d.ExternalId == "switch3-1").Endpoints.Select(e => e.Name).ShouldBe(["Teto", "Arandelas", "Varanda"]);
        portuguese.Single(d => d.ExternalId == "motion-1").Name.ShouldBe("Sensor de presença do escritório");
        english.Single(d => d.ExternalId == "motion-1").Name.ShouldBe("Office motion sensor", "it sits in the office");
    }

    [Fact]
    public async Task The_runtime_reports_the_inventory_in_the_configured_language()
    {
        await using var sim = await SimulatedHarness.StartAsync("""{"preset":"basic","language":"pt-BR"}""");

        sim.Context.Devices.Select(d => d.Name).ShouldContain("Luz da sala");
    }

    [Theory]
    [InlineData("""{"language":"fr"}""")]
    [InlineData("""{"language":7}""")]
    public void Only_english_and_portuguese_are_accepted(string config)
    {
        Should.Throw<ArgumentException>(() => Describe(config));
    }
}

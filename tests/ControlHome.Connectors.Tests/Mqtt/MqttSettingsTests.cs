using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.Mqtt;
using ControlHome.Connectors.Mqtt.Model;
using ControlHome.Connectors.Mqtt.Transport;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Connectors.Tests.Mqtt;

/// <summary>The descriptor, configuration parsing and the small helpers (back-off, naming, conversions).</summary>
public sealed class MqttSettingsTests
{
    [Fact]
    public void The_descriptor_lists_the_connection_fields_with_the_password_secret()
    {
        var descriptor = new MqttConnectorFactory().Descriptor;

        descriptor.Key.ShouldBe("mqtt");
        descriptor.Category.ShouldBe(ConnectorCategory.Protocol);
        descriptor.Features.ShouldBe(new ConnectorFeatures { SetupFlow = true, PushInventory = true, PermitJoin = true });
        descriptor.ConfigFields.Select(f => f.Key).ShouldBe(
            ["host", "port", "useTls", "username", "password", "clientId", "discoveryPrefix", "zigbee2mqttBaseTopic", "tasmotaDiscovery"]);
        descriptor.ConfigFields.Where(f => f.Secret).Select(f => f.Key).ShouldBe(["password"]);
        descriptor.ConfigFields.Where(f => f.Required).Select(f => f.Key).ShouldBe(["host"]);
        new MqttConnectorFactory().CreateSetupFlow().ShouldNotBeNull();
    }

    [Fact]
    public void Defaults_apply_when_only_the_host_is_given()
    {
        var settings = MqttSettings.Parse(MqttFixtures.Parse("""{"host":"umbrel.local"}"""));

        settings.ShouldBe(new MqttSettings("umbrel.local", 1883, false, null, null, "homeassistant", "zigbee2mqtt", true));
        var id = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        settings.EffectiveClientId(id).ShouldBe("controlhome-01234567-89ab-cdef-0123-456789abcdef");
        settings.Endpoint.ShouldBe("umbrel.local:1883");
    }

    [Fact]
    public void Blank_prefixes_disable_their_adapters_and_values_are_trimmed()
    {
        var settings = MqttSettings.Parse(MqttFixtures.Parse("""
            {"host":" 192.168.1.10 ","port":8883,"useTls":true,"username":"ch","clientId":" my-client ",
             "discoveryPrefix":"","zigbee2mqttBaseTopic":"/z2m/","tasmotaDiscovery":false,"password":"ignored here"}
            """));

        settings.ShouldBe(new MqttSettings("192.168.1.10", 8883, true, "ch", "my-client", null, "z2m", false));
        settings.EffectiveClientId(Guid.NewGuid()).ShouldBe("my-client");
    }

    [Theory]
    [InlineData("""{"port":1883}""", "host")]
    [InlineData("""{"host":"mqtt://broker"}""", "host")]
    [InlineData("""{"host":"a b"}""", "host")]
    [InlineData("""{"host":"broker","port":0}""", "port")]
    [InlineData("""{"host":"broker","zigbee2mqttBaseTopic":"z2m/#"}""", "zigbee2mqttBaseTopic")]
    [InlineData("""{"host":"broker","discoveryPrefix":"ha/+/x"}""", "discoveryPrefix")]
    [InlineData("""{"host":"broker","unknown":1}""", "unknown")]
    public void Invalid_configurations_are_refused_with_the_field(string json, string field)
    {
        var exception = Should.Throw<InvalidMqttSettingsException>(() => MqttSettings.Parse(MqttFixtures.Parse(json)));

        exception.Field.ShouldBe(field);
        exception.ShouldBeAssignableTo<ArgumentException>();
    }

    [Fact]
    public void The_factory_refuses_an_invalid_config()
    {
        var instance = new IntegrationInstance(Guid.NewGuid(), "x", MqttFixtures.Parse("""{"port":1883}"""), EmptySecretStore.Instance);

        Should.Throw<ArgumentException>(() => new MqttConnectorFactory().CreateRuntime(instance));
    }

    [Fact]
    public void Reconnect_delays_double_up_to_the_maximum()
    {
        var backoff = new ReconnectBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), jitter: 0);

        Enumerable.Range(0, 9).Select(i => backoff.Next(i).TotalSeconds).ShouldBe([1, 2, 4, 8, 16, 32, 60, 60, 60]);
        backoff.Next(int.MaxValue).ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Reconnect_delays_are_jittered_within_bounds()
    {
        var backoff = new ReconnectBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), jitter: 0.2, new Random(7));

        var delays = Enumerable.Range(0, 200).Select(_ => backoff.Next(3).TotalSeconds).ToList();

        delays.ShouldAllBe(d => d >= 6.4 && d <= 9.6);
        delays.Distinct().Count().ShouldBeGreaterThan(10);
        Enumerable.Range(0, 100).Select(_ => backoff.Next(20)).ShouldAllBe(d => d <= TimeSpan.FromSeconds(60));
    }

    [Theory]
    [InlineData("local_temperature_calibration", "localTemperatureCalibration")]
    [InlineData("ApparentPower", "apparentPower")]
    [InlineData("PM2.5", "pm25")]
    [InlineData("linkquality", "linkquality")]
    [InlineData("Dew point", "dewPoint")]
    [InlineData("2nd_sensor", "v2ndSensor")]
    [InlineData("__", "value")]
    public void Vendor_names_become_camel_case_keys(string name, string key)
    {
        Naming.CamelCase(name).ShouldBe(key);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(120, 100)]
    [InlineData(240, 100)]
    [InlineData(60, 50)]
    [InlineData(300, 75)]
    public void Hue_and_saturation_survive_a_round_trip_through_xy(double hue, double saturation)
    {
        var (x, y) = Conversions.HsToXy(hue, saturation);
        var (h, s) = Conversions.XyToHs(x, y);

        Math.Abs(((h - hue + 540) % 360) - 180).ShouldBeLessThan(2.5);
        s.ShouldBe(saturation, tolerance: 2);
    }

    [Fact]
    public void Units_convert_to_the_canonical_ones()
    {
        Conversions.ToCanonical("temperature", "°F")!(212).ShouldBe(100, tolerance: 1e-9);
        Conversions.ToCanonical("energy", "kWh")!(1.5).ShouldBe(1500);
        Conversions.ToCanonical("power", "kW")!(0.25).ShouldBe(250);
        Conversions.ToCanonical("voltage", "mV")!(3005).ShouldBe(3.005, tolerance: 1e-9);
        Conversions.ToCanonical("temperature", "furlongs").ShouldBeNull();
        Conversions.MiredToKelvin(250).ShouldBe(4000);
        Conversions.KelvinToMired(2700).ShouldBe(370);
    }

    [Theory]
    [InlineData("zigbee2mqtt/#", "zigbee2mqtt/bridge/devices", true)]
    [InlineData("zigbee2mqtt/#", "zigbee2mqtt", true)]
    [InlineData("tasmota/discovery/+/config", "tasmota/discovery/AA/config", true)]
    [InlineData("tasmota/discovery/+/config", "tasmota/discovery/AA/sensors", false)]
    [InlineData("a/b", "a/b/c", false)]
    [InlineData("homeassistant/#", "home/x", false)]
    public void Topic_filters_match_like_a_broker(string filter, string topic, bool matches)
    {
        TopicFilter.Matches(filter, topic).ShouldBe(matches);
    }

    [Theory]
    [InlineData("kitchen/climate/state", true)]
    [InlineData("zigbee2mqtt/living room/ceiling", true)]
    [InlineData("$SYS/broker/uptime", true)]
    [InlineData("/leading/slash", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("#", false)]
    [InlineData("bad/#/state", false)]
    [InlineData("home/+/temp", false)]
    [InlineData("plug+1/state", false)]
    public void Only_topic_names_are_valid_device_topics(string? topic, bool valid)
    {
        MqttTopic.IsValidName(topic).ShouldBe(valid);
    }

    [Fact]
    public void A_topic_name_has_no_nul_and_is_at_most_65535_utf8_bytes()
    {
        MqttTopic.IsValidName("nul\0char").ShouldBeFalse();
        MqttTopic.IsValidName(new string('a', MqttTopic.MaxLength)).ShouldBeTrue();
        MqttTopic.IsValidName(new string('a', MqttTopic.MaxLength + 1)).ShouldBeFalse();
        MqttTopic.IsValidName(new string('é', (MqttTopic.MaxLength / 2) + 1)).ShouldBeFalse(); // 2 bytes each
    }

    [Fact]
    public void Generic_writable_attributes_get_setters_bound_to_them()
    {
        var builder = new DeviceBuilder();
        var endpoint = builder.Endpoint("main");

        var first = endpoint.AddGeneric("mode", new EnumSchema(["a", "b"]), writable: true);
        var second = endpoint.AddGeneric("mode", BoolSchema.Instance, writable: true);

        (first, second).ShouldBe(("mode", "mode2"));
        var generic = builder.Build().Single().Capabilities.Single().GenericDefinition!;
        generic.FindSetter("mode")!.Name.ShouldBe("setMode");
        generic.FindSetter("mode2")!.Name.ShouldBe("setMode2");
        endpoint.SetterOf("mode2").ShouldBe("setMode2");
    }
}

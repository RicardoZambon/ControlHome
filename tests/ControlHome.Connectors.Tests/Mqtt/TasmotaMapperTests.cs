using ControlHome.Connectors.Mqtt.Model;
using ControlHome.Connectors.Mqtt.Tasmota;
using ControlHome.Connectors.Mqtt.Transport;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;

namespace ControlHome.Connectors.Tests.Mqtt;

/// <summary>Tasmota native discovery (fixtures) → devices, readings and commands.</summary>
public sealed class TasmotaMapperTests
{
    [Fact]
    public void A_metering_plug_has_one_relay_and_energy_on_main()
    {
        var plug = TasmotaMapper.Map(MqttFixtures.Json("tasmota-plug-config.json"), MqttFixtures.Json("tasmota-plug-sensors.json"))!;
        var device = plug.Discovered;

        device.ExternalId.ShouldBe("tasmota:DC4F22A1B2C3");
        device.Name.ShouldBe("Coffee plug");
        device.Manufacturer.ShouldBe("Tasmota");
        device.Model.ShouldBe("Sonoff Pow R2");
        device.Firmware.ShouldBe("14.2.0");
        device.DeviceClass.ShouldBe(DeviceClasses.Outlet);
        device.Connections["mac"].ShouldBe("DC:4F:22:A1:B2:C3");
        device.Endpoints.Select(e => e.Key).ShouldBe(["main"]);
        device.Capabilities().ShouldBe([CapabilityIds.Switch, CapabilityIds.Energy, CapabilityIds.Power, CapabilityIds.Generic]); // ENERGY order: Total before Power
        device.Generic().Attributes.Select(a => (a.Key, a.Unit)).ShouldBe(
        [
            ("energyYesterday", "Wh"), ("energyToday", "Wh"), ("apparentPower", "VA"), ("reactivePower", "var"),
            ("powerFactor", null), ("voltage", "V"), ("current", "A"),
        ]);
        device.Generic().FindAttribute("energyToday")!.StateClass.ShouldBe(StateClass.TotalIncreasing);
        plug.Topics.ShouldBe(
        [
            "stat/tasmota_A1B2C3/RESULT", "tele/tasmota_A1B2C3/STATE", "tele/tasmota_A1B2C3/SENSOR", "tele/tasmota_A1B2C3/LWT",
            "stat/tasmota_A1B2C3/STATUS10", "stat/tasmota_A1B2C3/STATUS8", "stat/tasmota_A1B2C3/POWER", "stat/tasmota_A1B2C3/POWER1",
        ],
            ignoreOrder: true);
    }

    [Fact]
    public void Plug_messages_report_relay_energy_and_availability()
    {
        var plug = TasmotaMapper.Map(MqttFixtures.Json("tasmota-plug-config.json"), MqttFixtures.Json("tasmota-plug-sensors.json"))!;

        var sensor = plug.Read(MqttMessage.FromText(
            "tele/tasmota_A1B2C3/SENSOR",
            """{"Time":"2026-10-12T10:00:00","ENERGY":{"Total":12.5,"Yesterday":0.5,"Today":0.25,"Power":61,"ApparentPower":66,"ReactivePower":25,"Factor":0.92,"Voltage":228,"Current":0.29}}"""));
        sensor.Value(CapabilityIds.Power, "power").ShouldBe(StateValue.Number(61));
        sensor.Value(CapabilityIds.Energy, "energy").ShouldBe(StateValue.Number(12500));
        sensor.Value(CapabilityIds.Generic, "energyToday").ShouldBe(StateValue.Number(250));
        sensor.Value(CapabilityIds.Generic, "voltage").ShouldBe(StateValue.Number(228));
        plug.Discovered.ShouldAllFit(sensor);

        plug.Read(MqttMessage.FromText("stat/tasmota_A1B2C3/POWER", "ON")).Value(CapabilityIds.Switch, "on").ShouldBe(StateValue.True);
        plug.Read(MqttMessage.FromText("stat/tasmota_A1B2C3/RESULT", """{"POWER":"OFF"}""")).Value(CapabilityIds.Switch, "on").ShouldBe(StateValue.False);
        plug.Read(MqttMessage.FromText("tele/tasmota_A1B2C3/STATE", """{"Uptime":"0T01:00:00","POWER":"ON","Wifi":{"RSSI":80}}"""))
            .Value(CapabilityIds.Switch, "on").ShouldBe(StateValue.True);
        plug.Read(MqttMessage.FromText("stat/tasmota_A1B2C3/STATUS10", """{"StatusSNS":{"ENERGY":{"Power":5}}}"""))
            .Value(CapabilityIds.Power, "power").ShouldBe(StateValue.Number(5));
        plug.Read(MqttMessage.FromText("tele/tasmota_A1B2C3/LWT", "Online")).ShouldBe([new MqttReading.Reachability(Availability.Online)]);
        plug.Read(MqttMessage.FromText("tele/tasmota_A1B2C3/LWT", "Offline")).ShouldBe([new MqttReading.Reachability(Availability.Offline)]);
    }

    [Fact]
    public void Plug_commands_go_to_the_power_topic()
    {
        var plug = TasmotaMapper.Map(MqttFixtures.Json("tasmota-plug-config.json"), null)!;

        plug.Publishes(CapabilityIds.Switch, "turnOn").ShouldBe([new MqttPublish("cmnd/tasmota_A1B2C3/POWER", "ON")]);
        plug.Publishes(CapabilityIds.Switch, "toggle").ShouldBe([new MqttPublish("cmnd/tasmota_A1B2C3/POWER", "TOGGLE")]);
        plug.Refresh().ShouldBe([new MqttPublish("cmnd/tasmota_A1B2C3/STATE", string.Empty), new MqttPublish("cmnd/tasmota_A1B2C3/STATUS", "10")]);
        plug.Discovered.DeviceClass.ShouldBe(DeviceClasses.Switch); // no sensors message yet: no energy known
    }

    [Fact]
    public void Two_relays_and_two_sensors_follow_a_custom_full_topic()
    {
        var th = TasmotaMapper.Map(MqttFixtures.Json("tasmota-th-config.json"), MqttFixtures.Json("tasmota-th-sensors.json"))!;
        var device = th.Discovered;

        device.Endpoints.Select(e => (e.Key, e.Name)).ShouldBe([("relay1", "Pump"), ("relay2", "Fan"), ("main", null), ("ds18b20", "DS18B20")]);
        device.Capabilities("main").ShouldBe([CapabilityIds.Temperature, CapabilityIds.Humidity, CapabilityIds.Generic]);
        device.Capabilities("ds18b20").ShouldBe([CapabilityIds.Temperature]);
        device.Generic().Attributes.Select(a => a.Key).ShouldBe(["dewPoint"]);

        var readings = th.Read(MqttMessage.FromText(
            "garden_th/tele/SENSOR",
            """{"Time":"2026-10-12T10:00:00","AM2301":{"Temperature":24.3,"Humidity":60.1,"DewPoint":16},"DS18B20":{"Id":"01144A0CB2AA","Temperature":19.9},"TempUnit":"C"}"""));
        readings.Value("main", CapabilityIds.Temperature, "temperature").ShouldBe(StateValue.Number(24.3));
        readings.Value("main", CapabilityIds.Humidity, "humidity").ShouldBe(StateValue.Number(60.1));
        readings.Value("ds18b20", CapabilityIds.Temperature, "temperature").ShouldBe(StateValue.Number(19.9));
        device.ShouldAllFit(readings);

        var relays = th.Read(MqttMessage.FromText("garden_th/stat/RESULT", """{"POWER1":"ON","POWER2":"OFF"}"""));
        relays.Value("relay1", CapabilityIds.Switch, "on").ShouldBe(StateValue.True);
        relays.Value("relay2", CapabilityIds.Switch, "on").ShouldBe(StateValue.False);
        th.Read(MqttMessage.FromText("garden_th/stat/POWER2", "ON")).Value("relay2", CapabilityIds.Switch, "on").ShouldBe(StateValue.True);
        th.Publishes(CapabilityIds.Switch, "turnOff", endpoint: "relay2").ShouldBe([new MqttPublish("garden_th/cmnd/POWER2", "OFF")]);
    }

    [Fact]
    public void Fahrenheit_sensors_are_converted()
    {
        var th = TasmotaMapper.Map(
            MqttFixtures.Json("tasmota-th-config.json"),
            MqttFixtures.Parse("""{"sn":{"SI7021":{"Temperature":68.0,"Humidity":40},"TempUnit":"F"}}"""))!;

        var readings = th.ReadSensors(MqttFixtures.Parse("""{"SI7021":{"Temperature":68.0,"Humidity":40}}"""));

        readings.Value("main", CapabilityIds.Temperature, "temperature").ShouldBe(StateValue.Number(20));
    }

    [Fact]
    public void A_config_without_mac_or_topic_is_ignored()
    {
        TasmotaMapper.Map(MqttFixtures.Parse("""{"dn":"x"}"""), null).ShouldBeNull();
    }
}

using System.Text.Json;
using ControlHome.Connectors.DahuaHttp;
using ControlHome.Connectors.DahuaHttp.Model;
using ControlHome.Connectors.DahuaHttp.Protocol;

namespace ControlHome.Connectors.Tests.DahuaHttp;

/// <summary>Event texts, key/value answers, device-kind detection and channel selections.</summary>
public sealed class DahuaProtocolTests
{
    [Fact]
    public void An_event_line_gives_code_action_and_index()
    {
        var e = DahuaEventParser.Parse("Code=VideoMotion;action=Start;index=0").ShouldHaveSingleItem();

        (e.Code, e.Action, e.Index, e.Data).ShouldBe(("VideoMotion", "Start", (int?)0, (JsonElement?)null));
        e.IsStart.ShouldBeTrue();
    }

    [Fact]
    public void Multi_line_data_is_parsed_as_json()
    {
        var e = DahuaEventParser.Parse("Code=AccessControl;action=Pulse;index=0;data={\n \"Status\" : 1,\n \"UserID\" : \"101\",\n \"Method\" : 15\n}\n")
            .ShouldHaveSingleItem();

        e.IsPulse.ShouldBeTrue();
        e.DataInteger("status").ShouldBe(1);
        e.DataText("userid").ShouldBe("101");
        e.DataInteger("Method").ShouldBe(15);
    }

    [Fact]
    public void A_part_may_carry_several_events()
    {
        var events = DahuaEventParser.Parse("Code=VideoMotion;action=Stop;index=2\r\nCode=CrossRegionDetection;action=Stop;index=0\r\n");

        events.Select(e => (e.Code, e.Action, e.Index)).ShouldBe([("VideoMotion", "Stop", (int?)2), ("CrossRegionDetection", "Stop", (int?)0)]);
    }

    [Theory]
    [InlineData("Heartbeat")]
    [InlineData("  heartbeat \r\n")]
    [InlineData("")]
    [InlineData("not an event at all")]
    [InlineData("action=Start;index=0")]
    public void Heartbeats_and_noise_yield_no_events(string body) => DahuaEventParser.Parse(body).ShouldBeEmpty();

    [Fact]
    public void Broken_data_drops_only_the_data()
    {
        var e = DahuaEventParser.Parse("Code=DoorStatus;action=Pulse;index=0;data={\"Status\" : \"Open\"").ShouldHaveSingleItem();

        e.Code.ShouldBe("DoorStatus");
        e.Data.ShouldBeNull();
    }

    [Fact]
    public void Missing_or_odd_fields_are_tolerated()
    {
        var e = DahuaEventParser.Parse("Code=AlarmLocal;;action=Start;index=x").ShouldHaveSingleItem();

        (e.Code, e.Action, e.Index).ShouldBe(("AlarmLocal", "Start", (int?)null));
    }

    [Fact]
    public void Key_values_parse_lines_and_tables()
    {
        var answer = DahuaKeyValues.Parse("type=VIP 1230 B G4\r\nversion=2.800.00IB000.0.R, Build Date 2023-03-01\r\n");

        answer["type"].ShouldBe("VIP 1230 B G4");
        answer["VERSION"].ShouldBe("2.800.00IB000.0.R, Build Date 2023-03-01");
        answer["missing"].ShouldBeNull();
        DahuaKeyValues.Parse("OK\r\n").IsOk.ShouldBeTrue();
        DahuaKeyValues.Parse("Error\r\nBad Request!\r\n").IsOk.ShouldBeFalse();
    }

    [Fact]
    public void Channel_titles_are_by_one_based_channel()
    {
        var titles = DahuaKeyValues.Parse(DahuaFixtures.Text("channel-title-recorder.txt")).ChannelTitles();

        titles.ShouldBe(new SortedDictionary<int, string> { [1] = "Garagem", [2] = "Portao", [3] = "Quintal", [4] = string.Empty });
    }

    [Fact]
    public void Only_enabled_smart_rules_count()
    {
        var rules = DahuaKeyValues.Parse(DahuaFixtures.Text("video-analyse-rule.txt")).EnabledRuleTypes();

        rules.Keys.Order().ShouldBe([1, 3]);
        rules[1].ShouldBe(["CrossLineDetection"], ignoreOrder: true);
        rules[3].ShouldBe(["CrossRegionDetection"], ignoreOrder: true);
    }

    [Fact]
    public void Active_event_channels_are_zero_based()
    {
        DahuaKeyValues.Parse("channels[0]=0\r\nchannels[1]=2\r\n").ChannelIndexes().ShouldBe([0, 2], ignoreOrder: true);
        DahuaKeyValues.Parse("Error\r\n").ChannelIndexes().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("VIP 1230 B G4", 1, "camera")]
    [InlineData("VIP 3230 D SL", 0, "camera")]
    [InlineData("iM5 SC", 1, "camera")]
    [InlineData("IPC-HFW2431S-S-S2", 1, "camera")]
    [InlineData("NVD 1404", 0, "recorder")]
    [InlineData("MHDX 1104", 4, "recorder")]
    [InlineData("iMHDX 3008", 8, "recorder")]
    [InlineData("DHI-NVR4104-P", 4, "recorder")]
    [InlineData("XVR5104C", 0, "recorder")]
    [InlineData("Some camera", 16, "recorder")]
    [InlineData("SS 3530 MF FACE", 1, "accessControl")]
    [InlineData("SS 5531 MF W", 0, "accessControl")]
    [InlineData("ASI7213X", 1, "accessControl")]
    [InlineData("DHI-ASI1201E", 0, "accessControl")]
    public void The_device_kind_is_detected_from_the_model_and_channels(string type, int channels, string expected) =>
        DahuaSettings.KindName(DahuaKindDetector.Detect(new DahuaIdentity(type, null, null, null, null), channels)).ShouldBe(expected);

    [Fact]
    public void Channel_selections_accept_numbers_and_ranges()
    {
        ChannelSelection.Parse("1-4, 6").ShouldNotBeNull().Channels.ShouldBe([1, 2, 3, 4, 6]);
        ChannelSelection.Parse("  ").ShouldBeNull();
        ChannelSelection.Parse(null).ShouldBeNull();
        Should.Throw<FormatException>(() => ChannelSelection.Parse("4-1"));
        Should.Throw<FormatException>(() => ChannelSelection.Parse("0"));
        Should.Throw<FormatException>(() => ChannelSelection.Parse("1-999"));
        Should.Throw<FormatException>(() => ChannelSelection.Parse("a"));
    }

    [Fact]
    public void Images_are_recognized_by_their_magic_bytes()
    {
        DahuaHttpClient.ImageType(DahuaFixtures.Jpeg).ShouldBe("image/jpeg");
        DahuaHttpClient.ImageType([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]).ShouldBe("image/png");
        DahuaHttpClient.ImageType("Error\r\n"u8).ShouldBeNull();
    }
}

using System.Text.Json;
using ControlHome.Domain.History;

namespace ControlHome.Domain.Tests.History;

/// <summary>
/// <see cref="DeviceEvent.StoredPayload(JsonElement?)"/>: event details become JSON that SQL Server's <c>json</c> type always
/// accepts — one payload it rejects would fail the whole write-behind flush (current state included) on every retry.
/// </summary>
public sealed class DeviceEventPayloadTests
{
    private static string? Stored(string json, JsonDocumentOptions options = default)
    {
        using var document = JsonDocument.Parse(json, options);
        return DeviceEvent.StoredPayload(document.RootElement);
    }

    [Fact]
    public void Objects_and_arrays_are_kept_and_scalars_wrapped()
    {
        Stored("""{ "count": 2, "button": "left", "held": false, "extra": null }""")
            .ShouldBe("""{"count":2,"button":"left","held":false,"extra":null}""");
        Stored("""[1, "two", [true]]""").ShouldBe("""[1,"two",[true]]""");
        Stored("42").ShouldBe("""{"value":42}""");
        Stored("\"ding\"").ShouldBe("""{"value":"ding"}""");
    }

    [Fact]
    public void Nothing_to_store_is_null()
    {
        DeviceEvent.StoredPayload((JsonElement?)null).ShouldBeNull();
        Stored("null").ShouldBeNull();
        DeviceEvent.StoredPayload((string?)null).ShouldBeNull();
        DeviceEvent.StoredPayload("not json").ShouldBeNull();
    }

    [Theory]
    [InlineData("""{"n":1E5}""", """{"n":100000}""")]
    [InlineData("""{"n":1.50}""", """{"n":1.50}""")]
    [InlineData("""{"n":-2.5e-3}""", """{"n":-0.0025}""")]
    [InlineData("""{"n":1e-7}""", """{"n":0.0000001}""")]
    [InlineData("""{"n":12345678901234567890}""", """{"n":12345678901234567890}""")]
    public void Numbers_are_written_as_plain_decimals(string json, string expected) => Stored(json).ShouldBe(expected);

    [Theory]
    [InlineData("""{"n":1e37}""")]                // SQL Server's json: "out of the range for numeric representation"
    [InlineData("""{"n":3.4028235e38}""")]        // a float sensor's "invalid" sentinel
    [InlineData("""{"n":123456789012345678901234567890123456789}""")]
    [InlineData("""{"n":1e400}""")]
    [InlineData("""[1, 2, 1.7976931348623157e308]""")]
    public void A_payload_with_a_number_too_large_to_store_is_left_out(string json) => Stored(json).ShouldBeNull();

    [Fact]
    public void What_only_a_lenient_parser_accepts_is_dropped()
    {
        Stored("""{"action":"single",} """, new JsonDocumentOptions { AllowTrailingCommas = true })
            .ShouldBe("""{"action":"single"}""");
        Stored("""{"a":1 /* note */}""", new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })
            .ShouldBe("""{"a":1}""");
    }

    [Fact]
    public void Too_deep_or_too_long_payloads_are_left_out()
    {
        Stored(new string('[', DeviceEvent.MaxPayloadDepth) + new string(']', DeviceEvent.MaxPayloadDepth)).ShouldNotBeNull();
        Stored(
                new string('[', DeviceEvent.MaxPayloadDepth + 1) + new string(']', DeviceEvent.MaxPayloadDepth + 1),
                new JsonDocumentOptions { MaxDepth = 256 })
            .ShouldBeNull();
        Stored(JsonSerializer.Serialize(new { text = new string('x', DeviceEvent.MaxPayloadLength) })).ShouldBeNull();
    }

    [Fact]
    public void Text_is_kept_readable()
    {
        Stored("""{"name":"Café été <b>","q":"say \"hi\""}""").ShouldBe("""{"name":"Café été <b>","q":"say \"hi\""}""");
    }

    [Fact]
    public void Storing_is_stable()
    {
        var once = Stored("""{"n":1E5,"list":[1.0, 2e-2],"s":"é"}""");

        DeviceEvent.StoredPayload(once).ShouldBe(once);
    }
}

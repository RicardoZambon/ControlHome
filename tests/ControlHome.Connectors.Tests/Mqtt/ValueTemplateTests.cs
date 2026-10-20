using ControlHome.Connectors.Mqtt.HomeAssistant;

namespace ControlHome.Connectors.Tests.Mqtt;

/// <summary>The supported subset of Home Assistant value and command templates.</summary>
public sealed class ValueTemplateTests
{
    [Theory]
    [InlineData(null, "21.5", "21.5")]
    [InlineData("{{ value }}", "ON", "ON")]
    [InlineData("{{value}}", "ON", "ON")]
    [InlineData("{{ value_json.temperature }}", """{"temperature":21.5}""", "21.5")]
    [InlineData("{{ value_json['temperature'] }}", """{"temperature":21.5}""", "21.5")]
    [InlineData("{{ value_json[\"temperature\"] }}", """{"temperature":21.5}""", "21.5")]
    [InlineData("{{ value_json.sensor.values[1] }}", """{"sensor":{"values":[1,2,3]}}""", "2")]
    [InlineData("{{ value_json.state }}", """{"state":"ON"}""", "ON")]
    [InlineData("{{ value_json.ok }}", """{"ok":true}""", "True")]
    [InlineData("{{ value | float }}", "21.50", "21.5")]
    [InlineData("{{ value | int }}", "21.7", "21")]
    [InlineData("{{ value_json.t | int }}", """{"t":-3.9}""", "-3")]
    [InlineData("{{ value_json.t | float | round(1) }}", """{"t":"21.46"}""", "21.5")]
    [InlineData("{{ value_json.t | round }}", """{"t":21.6}""", "22")]
    [InlineData("{{ value | float(0) }}", "unavailable", "0")]
    [InlineData("{{ value_json.state | lower }}", """{"state":"ON"}""", "on")]
    [InlineData("  {{ value_json.state | upper | trim }}  ", """{"state":" on "}""", "ON")]
    public void Supported_templates_render_like_jinja(string? template, string payload, string expected)
    {
        ValueTemplate.TryParse(template, out var parsed).ShouldBeTrue();

        parsed.Evaluate(payload)!.Text.ShouldBe(expected);
    }

    [Theory]
    [InlineData("{{ value_json.missing }}", """{"temperature":21.5}""")]
    [InlineData("{{ value_json.temperature }}", "not json")]
    [InlineData("{{ value | float }}", "unavailable")]
    [InlineData("{{ value_json.list[5] }}", """{"list":[1]}""")]
    public void Missing_values_produce_nothing(string template, string payload)
    {
        ValueTemplate.TryParse(template, out var parsed).ShouldBeTrue();

        parsed.Evaluate(payload).ShouldBeNull();
    }

    [Theory]
    [InlineData("{{ value_json.t }} °C")]
    [InlineData("{{ (value_json.t * 10) | round }}")]
    [InlineData("{{ value_json.t * 10 }}")]
    [InlineData("{{ 'ON' if value_json.on else 'OFF' }}")]
    [InlineData("{% if value == 'x' %}1{% endif %}")]
    [InlineData("{{ value_json.t | timestamp_local }}")]
    [InlineData("{{ states('sensor.x') }}")]
    [InlineData("{{ value_json.t | round(1, 'floor') }}")]
    [InlineData("{{ value.x }}")]
    [InlineData("{{ value_json.t ")]
    public void Anything_else_is_unsupported(string template)
    {
        ValueTemplate.TryParse(template, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null, "50", "50")]
    [InlineData("{{ value }}", "50", "50")]
    [InlineData("""{"brightness": {{ value | int }}}""", "50.4", """{"brightness": 50}""")]
    [InlineData("""{"state": "{{ value }}", "nested": {"a": 1}}""", "ON", """{"state": "ON", "nested": {"a": 1}}""")]
    public void Command_templates_substitute_the_value(string? template, string value, string expected)
    {
        CommandTemplate.TryParse(template, out var parsed).ShouldBeTrue();

        parsed.Render(value).ShouldBe(expected);
    }

    [Fact]
    public void A_set_position_template_uses_the_position_variable()
    {
        CommandTemplate.TryParse("""{"position": {{ position }}}""", out var parsed, "position").ShouldBeTrue();

        parsed.Render("42").ShouldBe("""{"position": 42}""");
    }

    [Theory]
    [InlineData("{{ value_json.x }}")]
    [InlineData("{% if value %}ON{% endif %}")]
    [InlineData("{{ value * 2 }}")]
    public void Unsupported_command_templates_are_refused(string template)
    {
        CommandTemplate.TryParse(template, out _).ShouldBeFalse();
    }
}

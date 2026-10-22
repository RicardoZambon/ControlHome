using System.Text.Json;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Domain.Tests.Capabilities;

public sealed class CommandValidatorTests
{
    private const string AcConstraints = """
        { "attributes": {
            "mode": { "values": ["off", "cool", "heat", "auto", "fan"] },
            "fanMode": { "values": ["auto", "low", "high"] },
            "targetTemperature": { "min": 16, "max": 30, "step": 0.5, "dependsOn": "mode",
                                   "cases": { "heat": { "min": 16, "max": 26 }, "cool": { "min": 18 } } } } }
        """;

    private static CommandDefinition SetLevel => CapabilityCatalog.Level.FindCommand("setLevel")!;

    private static CommandDefinition SetTarget => CapabilityCatalog.Thermostat.FindCommand("setTargetTemperature")!;

    [Theory]
    [InlineData("""{"level":50}""")]
    [InlineData("""{"level":0,"transitionMs":500}""")]
    [InlineData("""{"level":100,"transitionMs":null}""")]
    public void Accepts_valid_arguments(string arguments)
    {
        CommandValidator.Validate(SetLevel, Json(arguments), null).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("""{}""", "level", ValueErrorKind.Required)]
    [InlineData("""{"level":null}""", "level", ValueErrorKind.Required)]
    [InlineData("""{"level":101}""", "level", ValueErrorKind.OutOfRange)]
    [InlineData("""{"level":-1}""", "level", ValueErrorKind.OutOfRange)]
    [InlineData("""{"level":50.5}""", "level", ValueErrorKind.InvalidType)]
    [InlineData("""{"level":"50"}""", "level", ValueErrorKind.InvalidType)]
    [InlineData("""{"level":true}""", "level", ValueErrorKind.InvalidType)]
    [InlineData("""{"level":50,"transitionMs":-5}""", "transitionMs", ValueErrorKind.OutOfRange)]
    [InlineData("""{"level":50,"brightness":3}""", "brightness", ValueErrorKind.Unknown)]
    public void Rejects_invalid_arguments(string arguments, string argument, ValueErrorKind kind)
    {
        var result = CommandValidator.Validate(SetLevel, Json(arguments), null);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldBe([new ArgumentError(argument, kind)]);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("5")]
    [InlineData("\"level\"")]
    public void Arguments_must_be_an_object(string arguments)
    {
        CommandValidator.Validate(SetLevel, Json(arguments), null).Errors.ShouldBe([new ArgumentError(null, ValueErrorKind.InvalidType)]);
    }

    [Fact]
    public void No_arguments_means_an_empty_object()
    {
        var turnOn = CapabilityCatalog.Switch.FindCommand("turnOn")!;

        CommandValidator.Validate(turnOn, default, null).IsValid.ShouldBeTrue();
        CommandValidator.Validate(turnOn, Json("null"), null).IsValid.ShouldBeTrue();
        CommandValidator.Validate(SetLevel, default, null).Errors.ShouldBe([new ArgumentError("level", ValueErrorKind.Required)]);
    }

    [Fact]
    public void Reports_every_invalid_argument()
    {
        var result = CommandValidator.Validate(SetLevel, Json("""{"transitionMs":"slow","x":1}"""), null);

        result.Errors.ShouldBe(
        [
            new ArgumentError("x", ValueErrorKind.Unknown),
            new ArgumentError("level", ValueErrorKind.Required),
            new ArgumentError("transitionMs", ValueErrorKind.InvalidType),
        ]);
    }

    [Theory]
    [InlineData("""{"mode":"cool"}""", null)]
    [InlineData("""{"mode":"dry"}""", ValueErrorKind.NotAllowed)] // in the catalogue, not in this AC's subset
    [InlineData("""{"mode":"turbo"}""", ValueErrorKind.NotAllowed)] // not in the catalogue at all
    [InlineData("""{"mode":1}""", ValueErrorKind.InvalidType)]
    public void Enum_values_must_be_in_the_schema_and_the_endpoint_subset(string arguments, ValueErrorKind? expected)
    {
        var setMode = CapabilityCatalog.Thermostat.FindCommand("setMode")!;

        var result = CommandValidator.Validate(setMode, Json(arguments), Json(AcConstraints));

        (result.Errors.SingleOrDefault()?.Kind).ShouldBe(expected);
        result.IsValid.ShouldBe(expected is null);
    }

    [Theory]
    [InlineData(22.5, null)]
    [InlineData(16, null)]
    [InlineData(30, null)]
    [InlineData(15.5, ValueErrorKind.OutOfRange)] // schema allows 5, the endpoint 16
    [InlineData(30.5, ValueErrorKind.OutOfRange)]
    [InlineData(22.3, ValueErrorKind.NotOnStep)]
    public void Endpoint_ranges_and_steps_narrow_the_schema(double target, ValueErrorKind? expected)
    {
        var result = CommandValidator.Validate(SetTarget, Arguments("targetTemperature", target), Json(AcConstraints));

        (result.Errors.SingleOrDefault()?.Kind).ShouldBe(expected);
        result.IsValid.ShouldBe(expected is null);
    }

    [Theory]
    [InlineData("heat", 27, ValueErrorKind.OutOfRange)]
    [InlineData("heat", 26, null)]
    [InlineData("cool", 17, ValueErrorKind.OutOfRange)]
    [InlineData("cool", 29, null)]
    [InlineData("fan", 17, null)] // no case for fan: the base range applies
    [InlineData(null, 17, null)] // mode unknown: the base range applies
    public void Mode_dependent_ranges_use_the_current_mode(string? mode, double target, ValueErrorKind? expected)
    {
        var state = mode is null ? null : new Dictionary<string, StateValue> { ["mode"] = StateValue.String(mode) };

        var result = CommandValidator.Validate(SetTarget, Arguments("targetTemperature", target), Json(AcConstraints), state);

        (result.Errors.SingleOrDefault()?.Kind).ShouldBe(expected);
        result.IsValid.ShouldBe(expected is null);
    }

    [Fact]
    public void Strings_respect_max_length_and_allowed_values()
    {
        var selectSource = CapabilityCatalog.MediaPlayer.FindCommand("selectSource")!;
        var constraints = Json("""{"attributes":{"source":{"values":["HDMI1","TV"]}}}""");

        CommandValidator.Validate(selectSource, Json("""{"source":"HDMI1"}"""), constraints).IsValid.ShouldBeTrue();
        CommandValidator.Validate(selectSource, Json("""{"source":"HDMI3"}"""), constraints).Errors.Single().Kind.ShouldBe(ValueErrorKind.NotAllowed);
        CommandValidator.Validate(selectSource, Arguments("source", new string('x', 129)), null).Errors.Single().Kind.ShouldBe(ValueErrorKind.TooLong);
    }

    [Fact]
    public void Flags_must_be_distinct_known_names()
    {
        var command = new CommandDefinition(
            "setDays",
            [new ArgumentDefinition("days", new FlagsSchema(["mon", "tue", "wed"]), Required: true)],
            ConfirmationMode.AwaitState,
            null,
            SafetyCritical: false,
            FailSafe: false);

        CommandValidator.Validate(command, Json("""{"days":["mon","wed"]}"""), null).IsValid.ShouldBeTrue();
        CommandValidator.Validate(command, Json("""{"days":["mon","mon"]}"""), null).Errors.Single().Kind.ShouldBe(ValueErrorKind.NotAllowed);
        CommandValidator.Validate(command, Json("""{"days":["sun"]}"""), null).Errors.Single().Kind.ShouldBe(ValueErrorKind.NotAllowed);
        CommandValidator.Validate(command, Json("""{"days":"mon"}"""), null).Errors.Single().Kind.ShouldBe(ValueErrorKind.InvalidType);
    }

    [Fact]
    public void Malformed_constraints_throw()
    {
        Should.Throw<JsonException>(() => CommandValidator.Validate(SetLevel, Json("""{"level":5}"""), Json("[]")));
    }

    [Fact]
    public void State_values_are_checked_against_schemas()
    {
        ValueSchemaValidator.Accepts(CapabilityCatalog.Level.Attributes[0].Schema, StateValue.Number(40)).ShouldBeTrue();
        ValueSchemaValidator.Accepts(CapabilityCatalog.Level.Attributes[0].Schema, StateValue.Number(140)).ShouldBeFalse();
        ValueSchemaValidator.Accepts(CapabilityCatalog.Level.Attributes[0].Schema, StateValue.Null).ShouldBeTrue();
        ValueSchemaValidator.Accepts(CapabilityCatalog.Switch.Attributes[0].Schema, StateValue.String("on")).ShouldBeFalse();
        ValueSchemaValidator.Accepts(CapabilityCatalog.Button.Attributes[0].Schema, StateValue.String("double")).ShouldBeTrue();
        ValueSchemaValidator.Accepts(CapabilityCatalog.Button.Attributes[0].Schema, StateValue.String("triple")).ShouldBeFalse();
        ValueSchemaValidator.Accepts(JsonValueSchema.Instance, StateValue.Json(Json("""{"a":1}"""))).ShouldBeTrue();
    }

    private static JsonElement Arguments(string key, object value) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object> { [key] = value });

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

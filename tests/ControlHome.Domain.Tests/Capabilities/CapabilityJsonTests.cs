using System.Text.Json;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Domain.Tests.Capabilities;

public sealed class CapabilityJsonTests
{
    /// <summary>The API's settings (web defaults, strict numbers), without the stored-data tolerance for member order.</summary>
    private static readonly JsonSerializerOptions _web = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict,
    };

    public static TheoryData<ValueSchema, string> Schemas() => new()
    {
        { BoolSchema.Instance, """{"type":"bool"}""" },
        { new NumberSchema(0, 100, 1, "%", Integer: true), """{"type":"number","min":0,"max":100,"step":1,"unit":"%","integer":true}""" },
        { new NumberSchema(), """{"type":"number","min":null,"max":null,"step":null,"unit":null,"integer":false}""" },
        { new EnumSchema(["off", "cool"]), """{"type":"enum","values":["off","cool"]}""" },
        { new StringSchema(64), """{"type":"string","maxLength":64}""" },
        { new FlagsSchema(["a", "b"]), """{"type":"flags","flags":["a","b"]}""" },
        { JsonValueSchema.Instance, """{"type":"json"}""" },
        { new MomentarySchema { Events = ["single", "double"] }, """{"type":"momentary","events":["single","double"]}""" },
    };

    [Theory]
    [MemberData(nameof(Schemas))]
    public void Value_schemas_serialize_with_a_type_discriminator_and_round_trip(ValueSchema schema, string expectedJson)
    {
        var json = JsonSerializer.Serialize(schema, _web);

        json.ShouldBe(expectedJson);
        JsonSerializer.Deserialize<ValueSchema>(json, _web).ShouldBe(schema);
        CapabilityJson.Deserialize<ValueSchema>(CapabilityJson.Serialize(schema)).ShouldBe(schema);
    }

    [Fact]
    public void Stored_schemas_may_have_the_discriminator_anywhere()
    {
        var schema = CapabilityJson.Deserialize<ValueSchema>("""{"values":["a","b"],"type":"enum"}""");

        schema.ShouldBe(new EnumSchema(["a", "b"]));
    }

    [Fact]
    public void Unknown_schema_types_are_rejected()
    {
        Should.Throw<JsonException>(() => CapabilityJson.Deserialize<ValueSchema>("""{"type":"color"}"""));
    }

    [Fact]
    public void Every_catalogue_capability_round_trips()
    {
        foreach (var capability in CapabilityCatalog.All)
        {
            var json = CapabilityJson.Serialize(capability);

            CapabilityJson.Deserialize<CapabilityDefinition>(json).ShouldBe(capability, json);
        }
    }

    [Fact]
    public void A_capability_definition_uses_camel_case_and_string_enums()
    {
        var json = JsonSerializer.SerializeToElement(CapabilityCatalog.Energy, _web);

        var attribute = json.GetProperty("attributes")[0];
        attribute.GetProperty("key").GetString().ShouldBe("energy");
        attribute.GetProperty("stateClass").GetString().ShouldBe("total_increasing");
        attribute.GetProperty("category").GetString().ShouldBe("primary");
        attribute.GetProperty("schema").GetProperty("type").GetString().ShouldBe("number");

        var command = JsonSerializer.SerializeToElement(CapabilityCatalog.Cover.FindCommand("open"), _web);
        command.GetProperty("confirmation").GetString().ShouldBe("awaitState");
        command.GetProperty("safetyCriticalForDeviceClasses").EnumerateArray().Select(e => e.GetString()).ShouldBe(["gate", "garage"]);
    }

    [Fact]
    public void A_generic_definition_round_trips_with_timeouts_and_gates()
    {
        var definition = new CapabilityDefinition(
            CapabilityIds.Generic,
            1,
            [
                new AttributeDefinition("childLock", BoolSchema.Instance, null, StateClass.None, AttributeCategory.Primary, Recorded: false, Writable: true),
                new AttributeDefinition("filterLife", new NumberSchema(0, 100, 1, "%", true), "%", StateClass.Measurement, AttributeCategory.Diagnostic, true, false),
            ],
            [
                new CommandDefinition(
                    "setChildLock",
                    [new ArgumentDefinition("childLock", BoolSchema.Instance, Required: true)],
                    ConfirmationMode.AwaitState,
                    TimeSpan.FromSeconds(12),
                    SafetyCritical: false,
                    FailSafe: false) { GateAttribute = "childLock" },
            ]);

        var json = CapabilityJson.Serialize(definition);

        CapabilityJson.Deserialize<CapabilityDefinition>(json).ShouldBe(definition);
        definition.FindSetter("childLock")!.Name.ShouldBe("setChildLock");
    }

    [Fact]
    public void Constraints_parse_from_json_with_nested_cases()
    {
        using var document = JsonDocument.Parse("""
            { "attributes": {
                "mode": { "values": ["off", "cool"] },
                "targetTemperature": { "min": 16, "max": 30, "step": 0.5, "dependsOn": "mode",
                                       "cases": { "heat": { "min": 16, "max": 26 } } } } }
            """);

        var constraints = CapabilityConstraints.Parse(document.RootElement);

        constraints.For("mode")!.Values.ShouldBe(["off", "cool"]);
        var target = constraints.For("targetTemperature")!;
        target.Min.ShouldBe(16);
        target.Step.ShouldBe(0.5);
        target.DependsOn.ShouldBe("mode");
        target.Cases!["heat"].Max.ShouldBe(26);
        constraints.For("fanMode").ShouldBeNull();
    }

    [Fact]
    public void Missing_or_null_constraints_mean_none_and_malformed_ones_are_rejected()
    {
        CapabilityConstraints.Parse(null).ShouldBeSameAs(CapabilityConstraints.None);
        CapabilityConstraints.Parse(JsonDocument.Parse("null").RootElement).ShouldBeSameAs(CapabilityConstraints.None);
        Should.Throw<JsonException>(() => CapabilityConstraints.Parse(JsonDocument.Parse("[1]").RootElement));
        CapabilityConstraints.TryParse(JsonDocument.Parse("""{"attributes":{"x":{"min":"low"}}}""").RootElement, out var none).ShouldBeFalse();
        none.ShouldBeSameAs(CapabilityConstraints.None);
    }
}

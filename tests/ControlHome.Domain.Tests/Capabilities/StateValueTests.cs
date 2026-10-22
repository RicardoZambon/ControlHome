using System.Text.Json;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Domain.Tests.Capabilities;

public sealed class StateValueTests
{
    private static readonly JsonSerializerOptions _web = new(JsonSerializerDefaults.Web);

    public static TheoryData<StateValue, string> Values() => new()
    {
        { StateValue.Null, """{"kind":"null"}""" },
        { StateValue.True, """{"kind":"bool","b":true}""" },
        { StateValue.Bool(false), """{"kind":"bool","b":false}""" },
        { StateValue.Number(21.5), """{"kind":"number","n":21.5}""" },
        { StateValue.Number(-3), """{"kind":"number","n":-3}""" },
        { StateValue.String("cool"), """{"kind":"string","s":"cool"}""" },
        { StateValue.Json(Parse("""{"a":[1,2]}""")), """{"kind":"json","j":{"a":[1,2]}}""" },
        { StateValue.Json(Parse("[true]")), """{"kind":"json","j":[true]}""" },
    };

    [Theory]
    [MemberData(nameof(Values))]
    public void Serializes_as_a_tagged_value_and_round_trips(StateValue value, string expectedJson)
    {
        var json = JsonSerializer.Serialize(value, _web);

        json.ShouldBe(expectedJson);
        JsonSerializer.Deserialize<StateValue>(json, _web).ShouldBe(value);
        CapabilityJson.Deserialize<StateValue>(json).ShouldBe(value);
    }

    [Theory]
    [InlineData("""{"kind":"bool"}""")]
    [InlineData("""{"kind":"bool","b":true,"n":1}""")]
    [InlineData("""{"kind":"number","s":"1"}""")]
    [InlineData("""{"kind":"json","j":5}""")]
    [InlineData("""{"kind":"null","s":"x"}""")]
    public void Rejects_values_whose_members_do_not_match_the_kind(string json)
    {
        Should.Throw<Exception>(() => JsonSerializer.Deserialize<StateValue>(json, _web));
    }

    [Fact]
    public void Numbers_must_be_finite()
    {
        Should.Throw<ArgumentException>(() => StateValue.Number(double.NaN));
        Should.Throw<ArgumentException>(() => StateValue.Number(double.PositiveInfinity));
    }

    [Theory]
    [InlineData("true", StateValueKind.Bool)]
    [InlineData("false", StateValueKind.Bool)]
    [InlineData("12.25", StateValueKind.Number)]
    [InlineData("\"heat\"", StateValueKind.String)]
    [InlineData("null", StateValueKind.Null)]
    [InlineData("{\"x\":1}", StateValueKind.Json)]
    [InlineData("[1]", StateValueKind.Json)]
    public void Maps_plain_json_to_its_kind_and_back(string json, StateValueKind kind)
    {
        var value = StateValue.FromJson(Parse(json));

        value.Kind.ShouldBe(kind);
        JsonElement.DeepEquals(value.ToJson(), Parse(json)).ShouldBeTrue();
    }

    [Fact]
    public void Json_values_compare_structurally()
    {
        var a = StateValue.Json(Parse("""{"a":1,"b":[1,2]}"""));
        var b = StateValue.Json(Parse("""{ "a": 1, "b": [1, 2] }"""));

        a.ShouldBe(b);
        a.GetHashCode().ShouldBe(b.GetHashCode());
        a.ShouldBe(StateValue.Json(Parse("""{"b":[1,2],"a":1}"""))); // member order does not matter (stored JSON may be reordered)
        a.ShouldNotBe(StateValue.Json(Parse("""{"a":2,"b":[1,2]}""")));
        a.ShouldNotBe(StateValue.Json(Parse("""{"a":1,"b":[2,1]}""")));
    }

    [Fact]
    public void Equality_distinguishes_kinds_and_values()
    {
        StateValue.Number(1).ShouldNotBe(StateValue.True);
        StateValue.String("1").ShouldNotBe(StateValue.Number(1));
        StateValue.Number(1).ShouldBe(StateValue.Number(1.0));
        StateValue.String("a").ShouldBe(StateValue.String("a"));
        StateValue.Null.ShouldBe(StateValue.FromJson(default));
    }

    [Fact]
    public void Json_values_outlive_their_document()
    {
        StateValue value;
        using (var document = JsonDocument.Parse("""{"k":"v"}"""))
        {
            value = StateValue.Json(document.RootElement);
        }

        value.J!.Value.GetProperty("k").GetString().ShouldBe("v");
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

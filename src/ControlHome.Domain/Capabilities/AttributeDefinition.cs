using System.Text.Json.Serialization;

namespace ControlHome.Domain.Capabilities;

/// <summary>How an attribute's history is aggregated (the energy and statistics rollups depend on it).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StateClass>))]
public enum StateClass
{
    /// <summary>A state, not a quantity (on/off, a mode, a lock state).</summary>
    [JsonStringEnumMemberName("none")]
    None,

    /// <summary>A momentary measurement (W, °C, %): rolled up as time-weighted mean, min and max.</summary>
    [JsonStringEnumMemberName("measurement")]
    Measurement,

    /// <summary>An accumulated total that may go up and down (net energy).</summary>
    [JsonStringEnumMemberName("total")]
    Total,

    /// <summary>A counter that only grows, except when the device resets it (energy in Wh).</summary>
    [JsonStringEnumMemberName("total_increasing")]
    TotalIncreasing,
}

/// <summary>Whether the UI shows an attribute up front or among the device's diagnostics.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AttributeCategory>))]
public enum AttributeCategory
{
    [JsonStringEnumMemberName("primary")]
    Primary,

    /// <summary>Battery, link quality, signal strength and the like.</summary>
    [JsonStringEnumMemberName("diagnostic")]
    Diagnostic,
}

/// <summary>One value a capability exposes (an <c>Attribute</c> with an int <c>AttributeId</c> per endpoint).</summary>
/// <param name="Key">camelCase, unique within the capability (e.g. <c>on</c>, <c>targetTemperature</c>).</param>
/// <param name="Schema">The value's shape; connectors report values in its canonical unit.</param>
/// <param name="Unit">Canonical unit (see <see cref="Units"/>); equals the schema's unit for numbers.</param>
/// <param name="Recorded">Whether changes are appended to the history (<c>StateLog</c>).</param>
/// <param name="Writable">
/// The user can change it through one of the capability's commands. A generic UI finds that command with
/// <see cref="CapabilityDefinition.FindSetter"/> (the command whose only required argument is bound to this attribute);
/// catalogue capabilities with their own controls (e.g. <c>switch.on</c> via <c>turnOn</c>/<c>turnOff</c>) may have none.
/// </param>
public sealed record AttributeDefinition(
    string Key,
    ValueSchema Schema,
    string? Unit,
    StateClass StateClass,
    AttributeCategory Category,
    bool Recorded,
    bool Writable);

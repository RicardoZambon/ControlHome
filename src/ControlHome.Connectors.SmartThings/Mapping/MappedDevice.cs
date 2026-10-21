using System.Text.Json.Nodes;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.SmartThings.Model;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;

namespace ControlHome.Connectors.SmartThings.Mapping;

/// <summary>One attribute value to report.</summary>
/// <param name="ObservedAtUtc">The vendor's timestamp (never later than the mapping's now), or <c>null</c> for now.</param>
internal sealed record MappedReading(string Endpoint, string Capability, string Attribute, StateValue Value, DateTimeOffset? ObservedAtUtc);

/// <summary>A button's last reported press (<c>button.button</c>): a new timestamp means a new press.</summary>
internal sealed record ButtonObservation(string Endpoint, string Event, DateTimeOffset Timestamp);

/// <summary>One command for <c>POST /v1/devices/{id}/commands</c>.</summary>
internal sealed record StCommand(string Component, string Capability, string Command, IReadOnlyList<JsonNode?> Arguments)
{
    public JsonObject ToJson() => new()
    {
        ["component"] = Component,
        ["capability"] = Capability,
        ["command"] = Command,
        ["arguments"] = new JsonArray([.. Arguments.Select(a => a?.DeepClone())]),
    };
}

/// <summary>A device mapped to the canonical model, with what the command planner needs to address it.</summary>
/// <param name="MissingDefinitions">Capabilities without a catalogue mapping whose definitions are not loaded yet (left out until they are).</param>
internal sealed record MappedDevice(
    SmartThingsDevice Source,
    DiscoveredDevice Device,
    IReadOnlyList<MappedReading> Readings,
    Availability Availability,
    IReadOnlyDictionary<string, EndpointPlan> Endpoints,
    IReadOnlyList<CapabilityRef> MissingDefinitions,
    IReadOnlyList<ButtonObservation> Buttons,
    IReadOnlyList<PowerConsumptionWindow> EnergyWindows);

/// <summary>How the catalogue capabilities of one endpoint are backed by SmartThings capabilities of its component.</summary>
internal sealed class EndpointPlan(string component)
{
    public string Component { get; } = component;

    public bool Switch { get; set; }

    public bool Level { get; set; }

    public bool ColorHue { get; set; }

    public bool ColorTemperature { get; set; }

    public bool Lock { get; set; }

    public ThermostatPlan? Thermostat { get; set; }

    public MediaPlan? Media { get; set; }

    public AppliancePlan? Appliance { get; set; }

    public CoverPlan? Cover { get; set; }

    /// <summary>Generic commands by name.</summary>
    public Dictionary<string, GenericCommandPlan> Generic { get; } = new(StringComparer.Ordinal);
}

/// <summary>An air conditioner (<c>airConditionerMode</c>) or thermostat (<c>thermostatMode</c>).</summary>
/// <param name="ModeCapability"><c>airConditionerMode</c> or <c>thermostatMode</c>.</param>
/// <param name="ModeToVendor">Canonical mode → the vendor mode to send (no <c>off</c> when the switch turns it off).</param>
/// <param name="SwitchTurnsOff">The component's <c>switch</c> is the power: <c>off</c> turns it off, any other mode turns it on first.</param>
/// <param name="CoolingSetpoint">Whether <c>thermostatCoolingSetpoint</c> exists.</param>
/// <param name="HeatingSetpoint">Whether <c>thermostatHeatingSetpoint</c> exists.</param>
/// <param name="TemperatureConversion">The setpoints' vendor unit (°F → convert).</param>
/// <param name="SetpointStep">The vendor setpoint step (in the vendor unit), if known.</param>
/// <param name="FanToVendor">Canonical fan mode → vendor fan mode (<c>airConditionerFanMode</c>); empty without one.</param>
internal sealed record ThermostatPlan(
    string ModeCapability,
    IReadOnlyDictionary<string, string> ModeToVendor,
    bool SwitchTurnsOff,
    bool CoolingSetpoint,
    bool HeatingSetpoint,
    UnitConversion TemperatureConversion,
    double? SetpointStep,
    IReadOnlyDictionary<string, string> FanToVendor);

/// <summary>A TV, speaker or receiver.</summary>
/// <param name="InputCapability"><c>mediaInputSource</c> or <c>samsungvd.mediaInputSource</c>, if any.</param>
internal sealed record MediaPlan(
    bool Switch,
    bool Volume,
    bool Mute,
    string? InputCapability,
    bool Playback,
    bool TrackControl,
    bool LaunchApp);

/// <summary>A washer, dryer or dishwasher.</summary>
/// <param name="Capability">The operating-state capability commands go to.</param>
/// <param name="SetMachineState">It takes <c>setMachineState(run|pause|stop)</c>; otherwise <c>start</c>/<c>pause</c>/<c>cancel</c>.</param>
internal sealed record AppliancePlan(string Capability, bool SetMachineState);

/// <summary>A shade (<c>windowShade</c>) or a garage door / door (<c>doorControl</c>, <c>garageDoorControl</c>).</summary>
/// <param name="StateCapability">The capability with open/close (and, for shades, <c>pause</c>).</param>
/// <param name="PositionCapability"><c>windowShadeLevel</c> or <c>switchLevel</c> for <c>setPosition</c>, if any.</param>
internal sealed record CoverPlan(string StateCapability, bool CanPause, string? PositionCapability);

/// <summary>A generic command: a vendor command with positional arguments, or a value → command table.</summary>
internal sealed record GenericCommandPlan(
    string Capability,
    string? Command,
    IReadOnlyList<GenericArgumentPlan> Arguments,
    IReadOnlyDictionary<string, string>? CommandByValue);

/// <summary>One positional argument of a generic command.</summary>
/// <param name="Key">The argument's key in the canonical command.</param>
internal sealed record GenericArgumentPlan(string Key, bool Optional, UnitConversion Conversion);

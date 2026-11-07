using System.Text.Json;
using System.Text.Json.Nodes;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Connectors.SmartThings.Mapping;

/// <summary>
/// Turns canonical commands into SmartThings device commands (<c>component</c>, <c>capability</c>, <c>command</c>,
/// positional <c>arguments</c>) using what the mapper recorded about each endpoint. Pure; the caller sends them in
/// requests of at most <see cref="MaxCommandsPerRequest"/>.
/// </summary>
internal static class CommandPlanner
{
    /// <summary>SmartThings' guardrail for cloud-connected devices: at most 10 commands per request.</summary>
    public const int MaxCommandsPerRequest = 10;

    /// <summary>The vendor commands for <paramref name="batch"/>, in order, or why it cannot be sent.</summary>
    public static PlannedCommands Plan(MappedDevice device, DeviceCommandBatch batch)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(batch);
        var commands = new List<StCommand>();
        foreach (var command in batch.Commands)
        {
            if (!device.Endpoints.TryGetValue(command.EndpointKey, out var endpoint))
            {
                return PlannedCommands.Rejected(CommandRejectionCodes.UnknownEndpoint, $"The device has no endpoint '{command.EndpointKey}'.");
            }

            IReadOnlyList<StCommand>? planned = command.CapabilityId switch
            {
                CapabilityIds.Switch when endpoint.Switch => SwitchCommand(device, endpoint, command),
                CapabilityIds.Level when endpoint.Level && command.Command == "setLevel" =>
                    Number(command, "level") is { } level ? [Of(endpoint, "switchLevel", "setLevel", Math.Round(level))] : [],
                CapabilityIds.Color => ColorCommand(endpoint, command),
                CapabilityIds.Thermostat when endpoint.Thermostat is { } thermostat => ThermostatCommand(device, endpoint, thermostat, command),
                CapabilityIds.Lock when endpoint.Lock && command.Command is "lock" or "unlock" => [Of(endpoint, "lock", command.Command)],
                CapabilityIds.MediaPlayer when endpoint.Media is { } media => MediaCommand(endpoint, media, command),
                CapabilityIds.ApplianceCycle when endpoint.Appliance is { } appliance => ApplianceCommand(endpoint, appliance, command),
                CapabilityIds.Cover when endpoint.Cover is { } cover => CoverCommand(endpoint, cover, command),
                CapabilityIds.Generic => GenericCommand(endpoint, command),
                _ => null,
            };
            if (planned is null)
            {
                return PlannedCommands.Rejected(
                    CommandRejectionCodes.UnsupportedCommand,
                    $"SmartThings cannot run {command.CapabilityId}.{command.Command} on this endpoint.");
            }

            if (planned.Count == 0)
            {
                return PlannedCommands.Rejected(CommandRejectionCodes.InvalidArguments, $"The arguments of {command.CapabilityId}.{command.Command} cannot be sent to SmartThings.");
            }

            commands.AddRange(planned);
        }

        return commands.Count == 0
            ? PlannedCommands.Rejected(CommandRejectionCodes.InvalidArguments, "The batch has no commands.")
            : new PlannedCommands(commands, null);
    }

    /// <summary>The commands in requests of at most <see cref="MaxCommandsPerRequest"/>.</summary>
    public static IReadOnlyList<IReadOnlyList<StCommand>> Requests(IReadOnlyList<StCommand> commands) =>
        [.. commands.Chunk(MaxCommandsPerRequest).Select(chunk => (IReadOnlyList<StCommand>)chunk)];

    /// <summary>The body of <c>POST /v1/devices/{id}/commands</c>.</summary>
    public static JsonObject Body(IReadOnlyList<StCommand> commands) =>
        new() { ["commands"] = new JsonArray([.. commands.Select(c => (JsonNode)c.ToJson())]) };

    // An empty list means "known command, unusable arguments"; null means "not supported here".
    private static IReadOnlyList<StCommand>? SwitchCommand(MappedDevice device, EndpointPlan endpoint, DeviceCommand command) => command.Command switch
    {
        "turnOn" => [Of(endpoint, "switch", "on")],
        "turnOff" => [Of(endpoint, "switch", "off")],
        "toggle" => [Of(endpoint, "switch", Current(device, command.EndpointKey, CapabilityIds.Switch, "on")?.B == true ? "off" : "on")],
        _ => null,
    };

    private static IReadOnlyList<StCommand>? ColorCommand(EndpointPlan endpoint, DeviceCommand command)
    {
        switch (command.Command)
        {
            case "setColor" when endpoint.ColorHue:
                if (Number(command, "hue") is not { } hue || Number(command, "saturation") is not { } saturation)
                {
                    return [];
                }

                // SmartThings' hue is a percentage of the circle.
                var color = new JsonObject { ["hue"] = Math.Round(hue / 3.6, 2), ["saturation"] = Math.Round(saturation) };
                return [new StCommand(endpoint.Component, "colorControl", "setColor", [color])];
            case "setColorTemperature" when endpoint.ColorTemperature:
                return Number(command, "colorTemperature") is { } kelvin ? [Of(endpoint, "colorTemperature", "setColorTemperature", Math.Round(kelvin))] : [];
            default:
                return null;
        }
    }

    private static IReadOnlyList<StCommand>? ThermostatCommand(MappedDevice device, EndpointPlan endpoint, ThermostatPlan thermostat, DeviceCommand command)
    {
        switch (command.Command)
        {
            case "setMode":
                if (Text(command, "mode") is not { } mode)
                {
                    return [];
                }

                if (mode == "off" && thermostat.SwitchTurnsOff)
                {
                    return [Of(endpoint, "switch", "off")];
                }

                if (!thermostat.ModeToVendor.TryGetValue(mode, out var vendorMode))
                {
                    return [];
                }

                var setMode = thermostat.ModeCapability == "airConditionerMode" ? "setAirConditionerMode" : "setThermostatMode";
                return thermostat.SwitchTurnsOff
                    ? [Of(endpoint, "switch", "on"), Of(endpoint, thermostat.ModeCapability, setMode, vendorMode)]
                    : [Of(endpoint, thermostat.ModeCapability, setMode, vendorMode)];
            case "setTargetTemperature":
                if (Number(command, "targetTemperature") is not { } celsius)
                {
                    return [];
                }

                var value = StUnits.FromCanonical(celsius, thermostat.TemperatureConversion);
                value = thermostat.SetpointStep is { } step
                    ? StUnits.Snap(value, step)
                    : thermostat.TemperatureConversion == UnitConversion.FahrenheitToCelsius ? Math.Round(value) : value;
                var heat = Current(device, command.EndpointKey, CapabilityIds.Thermostat, "mode")?.S == "heat"
                    && thermostat.ModeCapability == "thermostatMode";
                return (heat && thermostat.HeatingSetpoint) || !thermostat.CoolingSetpoint
                    ? thermostat.HeatingSetpoint ? [Of(endpoint, "thermostatHeatingSetpoint", "setHeatingSetpoint", value)] : null
                    : [Of(endpoint, "thermostatCoolingSetpoint", "setCoolingSetpoint", value)];
            case "setFanMode":
                if (thermostat.FanToVendor.Count == 0)
                {
                    return null;
                }

                return Text(command, "fanMode") is { } fan && thermostat.FanToVendor.TryGetValue(fan, out var vendorFan)
                    ? [Of(endpoint, "airConditionerFanMode", "setFanMode", vendorFan)]
                    : [];
            default:
                return null;
        }
    }

    private static IReadOnlyList<StCommand>? MediaCommand(EndpointPlan endpoint, MediaPlan media, DeviceCommand command) => command.Command switch
    {
        "powerOn" when media.Switch => [Of(endpoint, "switch", "on")],
        "powerOff" when media.Switch => [Of(endpoint, "switch", "off")],
        "setVolume" when media.Volume => Number(command, "volume") is { } volume ? [Of(endpoint, "audioVolume", "setVolume", Math.Round(volume))] : [],
        "volumeUp" when media.Volume => [Of(endpoint, "audioVolume", "volumeUp")],
        "volumeDown" when media.Volume => [Of(endpoint, "audioVolume", "volumeDown")],
        "setMuted" when media.Mute => Flag(command, "muted") is { } muted ? [Of(endpoint, "audioMute", muted ? "mute" : "unmute")] : [],
        "selectSource" when media.InputCapability is { } input => Text(command, "source") is { } source ? [Of(endpoint, input, "setInputSource", source)] : [],
        "launchApp" when media.LaunchApp => Text(command, "app") is { } app ? [Of(endpoint, "custom.launchapp", "launchApp", app)] : [],
        "play" when media.Playback => [Of(endpoint, "mediaPlayback", "play")],
        "pause" when media.Playback => [Of(endpoint, "mediaPlayback", "pause")],
        "stop" when media.Playback => [Of(endpoint, "mediaPlayback", "stop")],
        "next" when media.TrackControl => [Of(endpoint, "mediaTrackControl", "nextTrack")],
        "previous" when media.TrackControl => [Of(endpoint, "mediaTrackControl", "previousTrack")],
        _ => null,
    };

    private static IReadOnlyList<StCommand>? ApplianceCommand(EndpointPlan endpoint, AppliancePlan appliance, DeviceCommand command) =>
        (command.Command, appliance.SetMachineState) switch
        {
            ("start", true) => [Of(endpoint, appliance.Capability, "setMachineState", "run")],
            ("pause", true) => [Of(endpoint, appliance.Capability, "setMachineState", "pause")],
            ("stop", true) => [Of(endpoint, appliance.Capability, "setMachineState", "stop")],
            ("start", false) => [Of(endpoint, appliance.Capability, "start")],
            ("pause", false) => [Of(endpoint, appliance.Capability, "pause")],
            ("stop", false) => [Of(endpoint, appliance.Capability, "cancel")],
            _ => null,
        };

    private static IReadOnlyList<StCommand>? CoverCommand(EndpointPlan endpoint, CoverPlan cover, DeviceCommand command) => command.Command switch
    {
        "open" => [Of(endpoint, cover.StateCapability, "open")],
        "close" => [Of(endpoint, cover.StateCapability, "close")],
        "stop" when cover.CanPause => [Of(endpoint, cover.StateCapability, "pause")],
        "setPosition" when cover.PositionCapability is { } position => Number(command, "position") is { } value
            ? [Of(endpoint, position, position == "windowShadeLevel" ? "setShadeLevel" : "setLevel", Math.Round(value))]
            : [],
        _ => null,
    };

    private static IReadOnlyList<StCommand>? GenericCommand(EndpointPlan endpoint, DeviceCommand command)
    {
        if (!endpoint.Generic.TryGetValue(command.Command, out var plan))
        {
            return null;
        }

        if (plan.CommandByValue is { } table)
        {
            // A value → command table (attribute set by argument-less commands).
            var value = command.Arguments.ValueKind == JsonValueKind.Object
                ? command.Arguments.EnumerateObject().Select(p => Json.AsText(p.Value)).FirstOrDefault()
                : null;
            return value is not null && table.TryGetValue(value, out var target) ? [new StCommand(endpoint.Component, plan.Capability, target, [])] : [];
        }

        var arguments = new List<JsonNode?>();
        var given = command.Arguments.ValueKind == JsonValueKind.Object ? command.Arguments : default;
        foreach (var argument in plan.Arguments)
        {
            if (given.ValueKind != JsonValueKind.Object || !given.TryGetProperty(argument.Key, out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                if (!argument.Optional)
                {
                    return [];
                }

                arguments.Add(null);
                continue;
            }

            arguments.Add(value.ValueKind == JsonValueKind.Number && argument.Conversion != UnitConversion.None
                ? JsonValue.Create(StUnits.FromCanonical(value.GetDouble(), argument.Conversion))
                : JsonNode.Parse(value.GetRawText()));
        }

        // Trailing optional arguments that were not given are left out.
        while (arguments.Count > 0 && arguments[^1] is null)
        {
            arguments.RemoveAt(arguments.Count - 1);
        }

        return [new StCommand(endpoint.Component, plan.Capability, plan.Command!, arguments)];
    }

    private static StCommand Of(EndpointPlan endpoint, string capability, string command) => new(endpoint.Component, capability, command, []);

    private static StCommand Of(EndpointPlan endpoint, string capability, string command, string argument) =>
        new(endpoint.Component, capability, command, [JsonValue.Create(argument)]);

    private static StCommand Of(EndpointPlan endpoint, string capability, string command, double argument) =>
        new(endpoint.Component, capability, command, [Math.Abs(argument - Math.Round(argument)) < 1e-9 ? JsonValue.Create((long)Math.Round(argument)) : JsonValue.Create(argument)]);

    private static StateValue? Current(MappedDevice device, string endpoint, string capability, string attribute) =>
        device.Readings.LastOrDefault(r => r.Endpoint == endpoint && r.Capability == capability && r.Attribute == attribute)?.Value;

    private static double? Number(DeviceCommand command, string key) =>
        command.Arguments.ValueKind == JsonValueKind.Object && command.Arguments.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static string? Text(DeviceCommand command, string key) =>
        command.Arguments.ValueKind == JsonValueKind.Object && command.Arguments.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? Flag(DeviceCommand command, string key) =>
        command.Arguments.ValueKind == JsonValueKind.Object && command.Arguments.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}

/// <summary>The planned vendor commands, or why there are none.</summary>
internal sealed record PlannedCommands(IReadOnlyList<StCommand> Commands, CommandOutcome? Rejection)
{
    public static PlannedCommands Rejected(string code, string message) => new([], CommandOutcome.Reject(code, message));
}

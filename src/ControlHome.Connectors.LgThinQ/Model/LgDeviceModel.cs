using System.Text.Json;
using System.Text.Json.Nodes;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Connectors.LgThinQ.Model;

/// <summary>One attribute value read from a device's status.</summary>
internal sealed record LgReading(string Endpoint, string Capability, string Attribute, StateValue Value);

/// <summary>Reads one attribute from a full (merged) status; <c>null</c> when the status does not tell.</summary>
internal delegate StateValue? LgReader(JsonElement status);

internal sealed record LgReadRule(string Endpoint, string Capability, string Attribute, LgReader Read);

/// <summary>
/// One control call: its payload and its target (sub-device, location and compartment): calls with the same target can be
/// merged into one.
/// </summary>
internal sealed record LgCall(JsonObject Payload, string Target);

/// <summary>The control calls one command needs (in order), or why it cannot be sent.</summary>
internal sealed record LgCommandResult(IReadOnlyList<LgCall> Payloads, CommandOutcome? Rejection)
{
    public static LgCommandResult Of(params LgCall[] payloads) => new(payloads, null);

    public static LgCommandResult Reject(string code, string message) => new([], CommandOutcome.Reject(code, message));

    public static LgCommandResult Unsupported(string message = "The appliance does not support this command.") =>
        Reject(CommandRejectionCodes.UnsupportedCommand, message);
}

/// <summary>Builds the control payloads of one command from its (validated) arguments and the device's current status.</summary>
internal delegate LgCommandResult LgCommandBuilder(JsonElement arguments, JsonElement status);

/// <summary>
/// A mapped LG device: its inventory, how its status becomes attribute values, how commands become control payloads, its
/// energy properties and where its notifications go. Immutable; a new profile gives a new model.
/// </summary>
internal sealed class LgDeviceModel(
    string deviceId,
    string deviceType,
    DiscoveredDevice discovered,
    IReadOnlyList<LgReadRule> reads,
    IReadOnlyDictionary<(string Endpoint, string Capability, string Command), LgCommandBuilder> commands,
    IReadOnlyList<(string Endpoint, string Property)> energy,
    IReadOnlyDictionary<string, string> notificationEndpoints)
{
    /// <summary>The generic momentary attribute that carries push notifications (<c>DEVICE_PUSH</c>).</summary>
    public const string NotificationAttribute = "notification";

    public string DeviceId { get; } = deviceId;

    public string DeviceType { get; } = deviceType;

    public DiscoveredDevice Discovered { get; } = discovered;

    /// <summary>The energy usage properties (energy profile) and the endpoint they belong to.</summary>
    public IReadOnlyList<(string Endpoint, string Property)> Energy { get; } = energy;

    /// <summary>Every attribute value <paramref name="status"/> tells (attributes it does not mention are left out).</summary>
    public IReadOnlyList<LgReading> Read(JsonElement status)
    {
        var readings = new List<LgReading>();
        foreach (var rule in reads)
        {
            StateValue? value;
            try
            {
                value = rule.Read(status);
            }
            catch (Exception exception) when (exception is InvalidOperationException or FormatException or ArgumentException or KeyNotFoundException)
            {
                value = null; // payload drift: skip the attribute, never the device
            }

            if (value is not null)
            {
                readings.Add(new LgReading(rule.Endpoint, rule.Capability, rule.Attribute, value));
            }
        }

        return readings;
    }

    /// <summary>The endpoint a push notification code belongs to (its sub-device's, else the first endpoint), or <c>null</c>.</summary>
    public string? NotificationEndpoint(string code) => notificationEndpoints.GetValueOrDefault(code);

    /// <summary>
    /// Checks every command of <paramref name="batch"/> against the inventory (endpoint, capability, command, arguments and
    /// constraints) and builds its control payloads; payloads for the same sub-device and location are merged into one call
    /// (several dependent properties in one control, as LG requires). Returns the calls in order, or a rejection.
    /// </summary>
    public (IReadOnlyList<LgCall>? Calls, CommandOutcome? Rejection) Plan(DeviceCommandBatch batch, JsonElement status)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Commands.Count == 0)
        {
            return (null, CommandOutcome.Reject(CommandRejectionCodes.InvalidArguments, "The batch has no commands."));
        }

        var calls = new List<LgCall>();
        foreach (var command in batch.Commands)
        {
            var endpoint = Discovered.Endpoints.FirstOrDefault(e => string.Equals(e.Key, command.EndpointKey, StringComparison.Ordinal));
            if (endpoint is null)
            {
                return (null, CommandOutcome.Reject(CommandRejectionCodes.UnknownEndpoint, "The appliance has no such endpoint."));
            }

            var binding = endpoint.Capabilities.FirstOrDefault(c => string.Equals(c.CapabilityId, command.CapabilityId, StringComparison.Ordinal));
            var definition = binding is null ? null : binding.GenericDefinition ?? CapabilityCatalog.Find(binding.CapabilityId);
            if (binding is null || definition?.FindCommand(command.Command) is not { } commandDefinition
                || !commands.TryGetValue((endpoint.Key, binding.CapabilityId, command.Command), out var build))
            {
                return (null, CommandOutcome.Reject(CommandRejectionCodes.UnsupportedCommand, "The appliance does not support this command."));
            }

            var validation = CommandValidator.Validate(commandDefinition, command.Arguments, binding.Constraints);
            if (!validation.IsValid)
            {
                return (null, CommandOutcome.Reject(
                    CommandRejectionCodes.InvalidArguments,
                    string.Join(", ", validation.Errors.Select(e => $"{e.Argument ?? "arguments"}: {e.Kind}"))));
            }

            var result = build(command.Arguments, status);
            if (result.Rejection is not null)
            {
                return (null, result.Rejection);
            }

            foreach (var call in result.Payloads)
            {
                // A single-call command for the same target as the previous call joins it.
                if (result.Payloads.Count == 1 && calls.Count > 0 && string.Equals(calls[^1].Target, call.Target, StringComparison.Ordinal))
                {
                    calls[^1] = calls[^1] with { Payload = (JsonObject)LgStatus.Merge(calls[^1].Payload.DeepClone(), call.Payload)! };
                }
                else
                {
                    calls.Add(call);
                }
            }
        }

        return (calls, null);
    }
}

using System.Text.Json;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.Mqtt.Transport;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;

namespace ControlHome.Connectors.Mqtt.Model;

/// <summary>What a message on one of a device's topics means.</summary>
internal abstract record MqttReading
{
    private MqttReading()
    {
    }

    /// <summary>A new attribute value (canonical unit).</summary>
    public sealed record State(string Endpoint, string Capability, string Attribute, StateValue Value) : MqttReading;

    /// <summary>A momentary event (button press).</summary>
    public sealed record Event(string Endpoint, string Capability, string Attribute, string Name) : MqttReading;

    /// <summary>The device's reachability.</summary>
    public sealed record Reachability(Availability Availability) : MqttReading;
}

/// <summary>One message to publish (QoS 1).</summary>
internal sealed record MqttPublish(string Topic, string Payload, bool Retain = false);

/// <summary>What executing a batch means on the wire.</summary>
/// <param name="Publishes">The messages, in order.</param>
/// <param name="Optimistic">
/// Readings to report once published, for entities without a state topic (the command result is then
/// <see cref="CommandOutcome.Completed"/>).
/// </param>
/// <param name="Final">
/// Nothing will confirm the command (a button press): it is <see cref="CommandOutcome.Completed"/> once published.
/// </param>
internal sealed record CommandPlan(IReadOnlyList<MqttPublish> Publishes, IReadOnlyList<MqttReading> Optimistic, bool Final = false)
{
    public static CommandPlan Of(params MqttPublish[] publishes) => new(publishes, []);

    /// <summary>Whether the command is done once published (no state report will confirm it).</summary>
    public bool CompletesOnPublish => Final || Optimistic.Count > 0;

    /// <summary>The plans of several commands, in order.</summary>
    public static CommandPlan Combine(IEnumerable<CommandPlan> plans)
    {
        var list = plans.ToList();
        return new CommandPlan(
            [.. list.SelectMany(p => p.Publishes)],
            [.. list.SelectMany(p => p.Optimistic)],
            list.Count > 0 && list.All(p => p.CompletesOnPublish));
    }
}

/// <summary>A command whose endpoint, capability, name and arguments were checked against the device's inventory.</summary>
internal sealed record ValidatedCommand(DeviceCommand Command, CommandDefinition Definition, CapabilityBinding Binding)
{
    public string Endpoint => Command.EndpointKey;

    public string Capability => Command.CapabilityId;

    public string Name => Command.Command;

    public JsonElement Arguments => Command.Arguments;

    public double Number(string argument) => Arguments.GetProperty(argument).GetDouble();

    public double? OptionalNumber(string argument) =>
        Arguments.ValueKind == JsonValueKind.Object && Arguments.TryGetProperty(argument, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    public string Text(string argument) => Arguments.GetProperty(argument).GetString()!;

    public JsonElement Argument(string argument) => Arguments.GetProperty(argument);
}

/// <summary>
/// A device one adapter discovered: its inventory, the topics it listens on, how to read them and how to turn commands into
/// publishes. Immutable: a changed discovery config produces a new instance.
/// </summary>
internal abstract class MqttDevice(DiscoveredDevice discovered)
{
    public DiscoveredDevice Discovered { get; } = discovered;

    public string ExternalId => Discovered.ExternalId;

    /// <summary>Exact topics carrying this device's state or availability (subscribed while the device exists).</summary>
    public abstract IReadOnlyCollection<string> Topics { get; }

    /// <summary>Whether a topic reports availability; otherwise the device counts as online once it reports state.</summary>
    public abstract bool HasAvailabilityTopic { get; }

    /// <summary>The meaning of <paramref name="message"/> (one of <see cref="Topics"/>); empty when it means nothing.</summary>
    public abstract IReadOnlyList<MqttReading> Read(MqttMessage message);

    /// <summary>Messages that make the device report its current state (empty when it cannot be asked).</summary>
    public virtual IReadOnlyList<MqttPublish> Refresh() => [];

    /// <summary>Readings known from the discovery config itself (reported right after the device is upserted).</summary>
    public virtual IReadOnlyList<MqttReading> InitialReadings => [];

    /// <summary>
    /// Checks every command of <paramref name="batch"/> against the inventory (endpoint, capability, command, arguments),
    /// then plans the publishes. Returns the plan or a rejection.
    /// </summary>
    public (CommandPlan? Plan, CommandOutcome? Rejection, bool SafetyCritical) Plan(DeviceCommandBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Commands.Count == 0)
        {
            return (null, CommandOutcome.Reject(CommandRejectionCodes.InvalidArguments, "The batch has no commands."), false);
        }

        var validated = new List<ValidatedCommand>(batch.Commands.Count);
        var safetyCritical = false;
        foreach (var command in batch.Commands)
        {
            var endpoint = Discovered.Endpoints.FirstOrDefault(e => string.Equals(e.Key, command.EndpointKey, StringComparison.Ordinal));
            if (endpoint is null)
            {
                return (null, CommandOutcome.Reject(CommandRejectionCodes.UnknownEndpoint, "The device has no such endpoint."), false);
            }

            var binding = endpoint.Capabilities.FirstOrDefault(c => string.Equals(c.CapabilityId, command.CapabilityId, StringComparison.Ordinal));
            var definition = binding is null ? null : binding.GenericDefinition ?? CapabilityCatalog.Find(binding.CapabilityId);
            if (binding is null || definition?.FindCommand(command.Command) is not { } commandDefinition)
            {
                return (null, Unsupported(), false);
            }

            var validation = CommandValidator.Validate(commandDefinition, command.Arguments, binding.Constraints);
            if (!validation.IsValid)
            {
                return (null, CommandOutcome.Reject(
                    CommandRejectionCodes.InvalidArguments,
                    string.Join(", ", validation.Errors.Select(e => $"{e.Argument ?? "arguments"}: {e.Kind}"))), false);
            }

            safetyCritical |= commandDefinition.IsSafetyCriticalFor(Discovered.DeviceClass);
            validated.Add(new ValidatedCommand(command, commandDefinition, binding));
        }

        var plan = PlanCommands(validated);
        return plan is null ? (null, Unsupported(), safetyCritical) : (plan, null, safetyCritical);
    }

    /// <summary>The publishes for already validated commands, or <c>null</c> when one of them is not supported by the device.</summary>
    protected abstract CommandPlan? PlanCommands(IReadOnlyList<ValidatedCommand> commands);

    protected static CommandOutcome Unsupported() =>
        CommandOutcome.Reject(CommandRejectionCodes.UnsupportedCommand, "The device does not support this command.");
}

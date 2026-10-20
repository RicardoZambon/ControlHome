using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.Mqtt.Model;
using ControlHome.Connectors.Mqtt.Transport;

namespace ControlHome.Connectors.Mqtt.HomeAssistant;

/// <summary>
/// A device assembled from Home Assistant discovery entities: every state/availability topic has readers (templates
/// already compiled), every supported command a planner.
/// </summary>
internal sealed class HaDevice(
    DiscoveredDevice discovered,
    IReadOnlyDictionary<string, IReadOnlyList<Func<string, IEnumerable<MqttReading>>>> readers,
    IReadOnlyDictionary<(string Endpoint, string Capability, string Command), Func<ValidatedCommand, CommandPlan?>> planners,
    bool hasAvailabilityTopic,
    IReadOnlyList<MqttReading> initialReadings) : MqttDevice(discovered)
{
    public override IReadOnlyCollection<string> Topics { get; } = [.. readers.Keys];

    public override bool HasAvailabilityTopic { get; } = hasAvailabilityTopic;

    public override IReadOnlyList<MqttReading> InitialReadings { get; } = initialReadings;

    public override IReadOnlyList<MqttReading> Read(MqttMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!readers.TryGetValue(message.Topic, out var topicReaders))
        {
            return [];
        }

        var payload = message.Text;
        return [.. topicReaders.SelectMany(read => read(payload))];
    }

    /// <summary>The readings of one payload on one topic (pure: used by the mapper tests).</summary>
    public IReadOnlyList<MqttReading> Read(string topic, string payload) => Read(MqttMessage.FromText(topic, payload));

    protected override CommandPlan? PlanCommands(IReadOnlyList<ValidatedCommand> commands)
    {
        var plans = new List<CommandPlan>();
        foreach (var command in commands)
        {
            if (!planners.TryGetValue((command.Endpoint, command.Capability, command.Name), out var plan) || plan(command) is not { } planned)
            {
                return null;
            }

            plans.Add(planned);
        }

        return CommandPlan.Combine(plans);
    }
}

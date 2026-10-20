using ControlHome.Connectors.Mqtt.Model;
using ControlHome.Connectors.Mqtt.Transport;
using Microsoft.Extensions.Logging;

namespace ControlHome.Connectors.Mqtt.HomeAssistant;

/// <summary>
/// Home Assistant MQTT discovery as an intake format (prefix e.g. <c>homeassistant</c>): configs are grouped into devices
/// by their <c>device</c> identifiers; a retained empty config removes its entities, and a device left without entities
/// is orphaned. After every connection the adapter publishes the birth message (<c>&lt;prefix&gt;/status</c> =
/// <c>online</c>) so devices that do not retain their configs announce them again. Configs published by Zigbee2MQTT's own
/// Home Assistant integration are skipped when the native Zigbee2MQTT adapter is on (no duplicates).
/// </summary>
internal sealed partial class HaDiscoveryAdapter(string prefix, bool skipZigbee2Mqtt, IMqttAdapterHost host) : IMqttAdapter
{
    /// <summary>The entities of each config topic (one, or several for device-based configs) and their device.</summary>
    private readonly Dictionary<string, HaDiscoveryMessage> _configs = new(StringComparer.Ordinal);

    public IReadOnlyList<string> TopicFilters => [prefix + "/#"];

    public async Task<bool> HandleAsync(MqttMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (HaDiscoveryParser.ParseTopic(prefix, message.Topic) is not { } topic)
        {
            return false;
        }

        _configs.TryGetValue(topic.Topic, out var previous);
        if (message.IsEmpty)
        {
            // Retained empty config: the entities are gone.
            if (previous is not null)
            {
                _configs.Remove(topic.Topic);
                await RebuildAsync(previous.DeviceKey, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }

        var parsed = HaDiscoveryParser.Parse(topic, message.Text);
        if (parsed is null)
        {
            LogInvalidConfig(host.Logger, message.Topic);
            return true;
        }

        if (parsed.FromZigbee2Mqtt && skipZigbee2Mqtt)
        {
            return true;
        }

        _configs[topic.Topic] = parsed;
        await RebuildAsync(parsed.DeviceKey, cancellationToken).ConfigureAwait(false);
        if (previous is not null && previous.DeviceKey != parsed.DeviceKey)
        {
            await RebuildAsync(previous.DeviceKey, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public async Task OnConnectedAsync(CancellationToken cancellationToken) =>
        await host.PublishAsync(new MqttPublish(prefix + "/status", "online"), cancellationToken).ConfigureAwait(false);

    private async Task RebuildAsync(string deviceKey, CancellationToken cancellationToken)
    {
        var configs = _configs.Values.Where(c => c.DeviceKey == deviceKey).OrderBy(c => c.Topic.Topic, StringComparer.Ordinal).ToList();
        var entities = configs.SelectMany(c => c.Entities).ToList();
        if (entities.Count == 0)
        {
            if (host.FindDevice(deviceKey) is not null)
            {
                await host.RemoveDeviceAsync(deviceKey, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        // The device block of the most complete config wins (single-component configs may repeat it partially).
        var device = configs.Select(c => c.Device).Where(d => d is not null).OrderByDescending(d => d!.Value.EnumerateObject().Count()).FirstOrDefault();
        var mapped = HaDeviceMapper.Map(deviceKey, device, entities);
        if (mapped.Discovered.Endpoints.Count == 0)
        {
            LogNothingMapped(host.Logger, deviceKey);
            if (host.FindDevice(deviceKey) is not null)
            {
                await host.RemoveDeviceAsync(deviceKey, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        await host.SetDeviceAsync(mapped, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "MQTT: ignored an invalid Home Assistant discovery config on {Topic}")]
    private static partial void LogInvalidConfig(ILogger logger, string topic);

    [LoggerMessage(Level = LogLevel.Information, Message = "MQTT: Home Assistant device {ExternalId} has no supported entities")]
    private static partial void LogNothingMapped(ILogger logger, string externalId);
}

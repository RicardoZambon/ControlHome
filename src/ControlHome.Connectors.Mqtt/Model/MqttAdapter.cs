using ControlHome.Connectors.Mqtt.Transport;
using Microsoft.Extensions.Logging;

namespace ControlHome.Connectors.Mqtt.Model;

/// <summary>What an adapter can do with the runtime it runs in.</summary>
internal interface IMqttAdapterHost
{
    ILogger Logger { get; }

    /// <summary>A device by external id, or <c>null</c>.</summary>
    MqttDevice? FindDevice(string externalId);

    /// <summary>
    /// Adds or replaces a device: upserts its inventory when it changed, subscribes its topics, replays retained messages
    /// already received for them and, for a device new in this session, asks it for its current state.
    /// </summary>
    Task SetDeviceAsync(MqttDevice device, CancellationToken cancellationToken);

    /// <summary>The device is gone upstream: it becomes orphaned (never deleted) and its topics are ignored.</summary>
    Task RemoveDeviceAsync(string externalId, CancellationToken cancellationToken);

    /// <summary>Reports readings of a device (e.g. ones learnt from a bridge message).</summary>
    void Report(MqttDevice device, IReadOnlyList<MqttReading> readings);

    /// <summary>Publishes a message; returns <c>false</c> when not connected.</summary>
    Task<bool> PublishAsync(MqttPublish message, CancellationToken cancellationToken);
}

/// <summary>One protocol over the shared broker connection (Zigbee2MQTT, Home Assistant discovery, Tasmota).</summary>
internal interface IMqttAdapter
{
    /// <summary>The wildcard subscriptions the adapter needs (device topics are subscribed per device).</summary>
    IReadOnlyList<string> TopicFilters { get; }

    /// <summary>Handles the adapter's own topics (discovery configs, bridge messages); <c>false</c> when the message is not one.</summary>
    Task<bool> HandleAsync(MqttMessage message, CancellationToken cancellationToken);

    /// <summary>Called after every (re)connection, once subscribed.</summary>
    Task OnConnectedAsync(CancellationToken cancellationToken);
}

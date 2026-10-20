using System.Text.Json;
using System.Text.Json.Nodes;
using ControlHome.Connectors.Mqtt.Model;
using ControlHome.Connectors.Mqtt.Transport;
using Microsoft.Extensions.Logging;

namespace ControlHome.Connectors.Mqtt.Zigbee2Mqtt;

/// <summary>
/// Zigbee2MQTT (native, base topic e.g. <c>zigbee2mqtt</c>): the retained <c>bridge/devices</c> is the inventory (devices
/// missing from a newer list are orphaned), <c>bridge/info</c> describes the bridge, <c>bridge/event</c>
/// <c>device_renamed</c> moves a device to its new friendly name at once. Device state and availability topics are
/// routed by the runtime to the <see cref="Z2mDevice"/>s.
/// </summary>
internal sealed partial class Z2mAdapter(string baseTopic, IMqttAdapterHost host) : IMqttAdapter
{
    private readonly string _prefix = baseTopic + "/";
    private readonly Dictionary<string, JsonElement> _entries = new(StringComparer.Ordinal);
    private string? _bridgeId;
    private JsonElement? _info;

    public IReadOnlyList<string> TopicFilters => [baseTopic + "/#"];

    public async Task<bool> HandleAsync(MqttMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!message.Topic.StartsWith(_prefix + "bridge/", StringComparison.Ordinal))
        {
            return false;
        }

        switch (message.Topic[_prefix.Length..])
        {
            case "bridge/devices":
                await OnDevicesAsync(message, cancellationToken).ConfigureAwait(false);
                break;
            case "bridge/info":
                await OnInfoAsync(message, cancellationToken).ConfigureAwait(false);
                break;
            case "bridge/event":
                await OnEventAsync(message, cancellationToken).ConfigureAwait(false);
                break;
            case "bridge/response/permit_join":
                if (Json.TryParse(message.Text) is { ValueKind: JsonValueKind.Object } response && response.Str("status") == "error")
                {
                    LogPermitJoinFailed(host.Logger, baseTopic, response.Str("error") ?? "unknown error");
                }

                break;
        }

        // Other bridge topics (state, logging, responses) are not inventory; bridge/state is read by the bridge device.
        return true;
    }

    public Task OnConnectedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Zigbee2MQTT 2.x: <c>bridge/request/permit_join</c> with <c>{"time": seconds}</c>.</summary>
    public static MqttPublish PermitJoinRequest(string baseTopic, int seconds) =>
        new($"{baseTopic}/bridge/request/permit_join", Json.Serialize(new JsonObject { ["time"] = Math.Clamp(seconds, 0, 254) }));

    private async Task OnDevicesAsync(MqttMessage message, CancellationToken cancellationToken)
    {
        if (Json.TryParse(message.Text) is not { ValueKind: JsonValueKind.Array } list)
        {
            if (!message.IsEmpty)
            {
                LogInvalidPayload(host.Logger, message.Topic);
            }

            return;
        }

        var entries = list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
        var coordinator = entries.FirstOrDefault(e => string.Equals(e.Str("type"), "Coordinator", StringComparison.OrdinalIgnoreCase));
        if (coordinator.ValueKind == JsonValueKind.Object && coordinator.Str("ieee_address") is { Length: > 0 } bridgeId)
        {
            _bridgeId = bridgeId;
            await SetBridgeAsync(cancellationToken).ConfigureAwait(false);
        }

        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (Z2mDeviceMapper.Map(entry, baseTopic, _bridgeId) is not { } device)
            {
                continue;
            }

            current.Add(device.ExternalId);
            _entries[device.ExternalId] = entry;
            await host.SetDeviceAsync(device, cancellationToken).ConfigureAwait(false);
        }

        // Devices that left the network (or were disabled) since the previous list are orphaned, never deleted.
        foreach (var gone in _entries.Keys.Where(id => !current.Contains(id)).ToList())
        {
            _entries.Remove(gone);
            await host.RemoveDeviceAsync(gone, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task OnInfoAsync(MqttMessage message, CancellationToken cancellationToken)
    {
        if (Json.TryParse(message.Text) is { ValueKind: JsonValueKind.Object } info)
        {
            _info = info;
            if (_bridgeId is not null)
            {
                await SetBridgeAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task SetBridgeAsync(CancellationToken cancellationToken)
    {
        var bridge = Z2mDeviceMapper.MapBridge(_bridgeId!, _info, baseTopic);
        await host.SetDeviceAsync(bridge, cancellationToken).ConfigureAwait(false);
        if (_info is { } info)
        {
            host.Report(bridge, bridge.ReadState(info));
        }
    }

    private async Task OnEventAsync(MqttMessage message, CancellationToken cancellationToken)
    {
        if (Json.TryParse(message.Text) is not { ValueKind: JsonValueKind.Object } @event
            || @event.Str("type") != "device_renamed"
            || @event.Get("data") is not { ValueKind: JsonValueKind.Object } data
            || data.Str("from") is not { Length: > 0 } from
            || data.Str("to") is not { Length: > 0 } to)
        {
            return;
        }

        // Move the device to its new topics right away (a new bridge/devices follows and confirms it).
        foreach (var (id, entry) in _entries.ToList())
        {
            if (entry.Str("friendly_name") != from || JsonNode.Parse(entry.GetRawText()) is not JsonObject renamed)
            {
                continue;
            }

            renamed["friendly_name"] = to;
            var element = JsonSerializer.SerializeToElement(renamed);
            _entries[id] = element;
            if (Z2mDeviceMapper.Map(element, baseTopic, _bridgeId) is { } device)
            {
                LogRenamed(host.Logger, id, from, to);
                await host.SetDeviceAsync(device, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "MQTT: ignored an invalid payload on {Topic}")]
    private static partial void LogInvalidPayload(ILogger logger, string topic);

    [LoggerMessage(Level = LogLevel.Information, Message = "Zigbee2MQTT renamed device {ExternalId} from {From} to {To}")]
    private static partial void LogRenamed(ILogger logger, string externalId, string from, string to);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Zigbee2MQTT ({BaseTopic}) refused to permit joining: {Error}")]
    private static partial void LogPermitJoinFailed(ILogger logger, string baseTopic, string error);
}

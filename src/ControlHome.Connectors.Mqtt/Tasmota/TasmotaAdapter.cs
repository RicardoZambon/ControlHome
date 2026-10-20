using System.Text.Json;
using ControlHome.Connectors.Mqtt.Model;
using ControlHome.Connectors.Mqtt.Transport;
using Microsoft.Extensions.Logging;

namespace ControlHome.Connectors.Mqtt.Tasmota;

/// <summary>
/// Tasmota native discovery (<c>tasmota/discovery/&lt;MAC&gt;/config</c> and <c>/sensors</c>, retained). A retained
/// empty config orphans the device. Device topics (<c>stat/…</c>, <c>tele/…</c>) are subscribed per device.
/// </summary>
internal sealed partial class TasmotaAdapter(IMqttAdapterHost host) : IMqttAdapter
{
    private const string Prefix = MqttSettings.TasmotaDiscoveryPrefix + "/";

    private readonly Dictionary<string, (JsonElement? Config, JsonElement? Sensors)> _discovery = new(StringComparer.Ordinal);

    public IReadOnlyList<string> TopicFilters => [MqttSettings.TasmotaDiscoveryPrefix + "/#"];

    public async Task<bool> HandleAsync(MqttMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!message.Topic.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var levels = message.Topic[Prefix.Length..].Split('/');
        if (levels is not [var mac, var kind and ("config" or "sensors")] || mac.Length == 0)
        {
            return true;
        }

        _discovery.TryGetValue(mac, out var current);
        var externalId = TasmotaMapper.ExternalIdPrefix + mac.ToUpperInvariant();
        if (kind == "config" && message.IsEmpty)
        {
            _discovery.Remove(mac);
            if (host.FindDevice(externalId) is not null)
            {
                await host.RemoveDeviceAsync(externalId, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }

        var json = Json.TryParse(message.Text);
        if (json is not { ValueKind: JsonValueKind.Object } && !message.IsEmpty)
        {
            LogInvalidPayload(host.Logger, message.Topic);
            return true;
        }

        current = kind == "config" ? (json, current.Sensors) : (current.Config, json);
        _discovery[mac] = current;
        if (current.Config is { } config && TasmotaMapper.Map(config, current.Sensors) is { } device)
        {
            await host.SetDeviceAsync(device, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public Task OnConnectedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "MQTT: ignored an invalid Tasmota discovery payload on {Topic}")]
    private static partial void LogInvalidPayload(ILogger logger, string topic);
}

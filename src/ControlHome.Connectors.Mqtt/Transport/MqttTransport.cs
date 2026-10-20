using System.Text;

namespace ControlHome.Connectors.Mqtt.Transport;

/// <summary>One received MQTT message (the payload is copied out of the client's buffers).</summary>
internal sealed record MqttMessage(string Topic, ReadOnlyMemory<byte> Payload, bool Retain)
{
    /// <summary>A zero-length payload (a retained empty message deletes a discovery config).</summary>
    public bool IsEmpty => Payload.IsEmpty;

    /// <summary>The payload as UTF-8 text.</summary>
    public string Text => Encoding.UTF8.GetString(Payload.Span);

    public static MqttMessage FromText(string topic, string payload, bool retain = false) => new(topic, Encoding.UTF8.GetBytes(payload), retain);
}

/// <summary>How to reach and authenticate with a broker.</summary>
/// <param name="Password">The secret; never logged or put in messages.</param>
/// <param name="Timeout">Connect / operation timeout.</param>
internal sealed record MqttConnectionOptions(
    string Host,
    int Port,
    bool UseTls,
    string? Username,
    string? Password,
    string ClientId,
    TimeSpan Timeout)
{
    /// <summary>Prints without the password.</summary>
    public override string ToString() => $"{Host}:{Port} (tls: {UseTls}, user: {Username ?? "-"}, client: {ClientId})";
}

/// <summary>Why a connection attempt failed.</summary>
internal enum MqttConnectFailure
{
    /// <summary>The broker could not be reached (DNS, refused TCP, TLS, timeout): retry later.</summary>
    Unreachable,

    /// <summary>The broker rejected the user name or password.</summary>
    BadCredentials,

    /// <summary>The broker does not allow this user (or anonymous access) to connect.</summary>
    NotAuthorized,

    /// <summary>Any other CONNACK refusal (client id rejected, server unavailable, protocol).</summary>
    Refused,
}

/// <summary>A failed connection attempt. The message never contains the password.</summary>
internal sealed class MqttConnectException(MqttConnectFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public MqttConnectFailure Failure { get; } = failure;
}

/// <summary>
/// A broker connection (one MQTT client). Not reconnecting by itself: the runtime creates a new transport for every
/// attempt and re-subscribes (MQTTnet v5 has no managed client). Publish/subscribe are thread-safe.
/// </summary>
internal interface IMqttTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>Called for every received message, in order, on the client's receive loop (keep it short).</summary>
    Func<MqttMessage, Task>? MessageReceived { get; set; }

    /// <summary>Called once when an established connection drops (with a short, non-secret reason).</summary>
    Func<string, Task>? Disconnected { get; set; }

    /// <exception cref="MqttConnectException">The broker cannot be reached or refused the connection.</exception>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Subscribes to topic filters (QoS 1); retained messages for them arrive afterwards.</summary>
    Task SubscribeAsync(IReadOnlyCollection<string> topicFilters, CancellationToken cancellationToken);

    /// <summary>Publishes a message (QoS 1).</summary>
    Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken);

    /// <summary>Disconnects cleanly (no-op when not connected).</summary>
    Task DisconnectAsync(CancellationToken cancellationToken);
}

/// <summary>Creates a transport for one connection attempt.</summary>
internal delegate IMqttTransport MqttTransportFactory(MqttConnectionOptions options);

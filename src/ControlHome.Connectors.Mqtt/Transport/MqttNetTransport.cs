using System.Buffers;
using System.Net.Sockets;
using System.Security.Authentication;
using MQTTnet;
using MQTTnet.Exceptions;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace ControlHome.Connectors.Mqtt.Transport;

/// <summary>
/// <see cref="IMqttTransport"/> over an MQTTnet v5 client: MQTT 3.1.1 (the widest broker support), clean session,
/// QoS 1, 30 s keep-alive. TLS uses the system's certificate validation.
/// </summary>
internal sealed class MqttNetTransport : IMqttTransport
{
    private static readonly MqttClientFactory _factory = new();

    private readonly IMqttClient _client;
    private readonly MqttClientOptions _options;
    private int _connected;

    private MqttNetTransport(MqttConnectionOptions options)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(options.Host, options.Port)
            .WithClientId(options.ClientId)
            .WithCleanSession(true)
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithTimeout(options.Timeout);
        if (options.Username is not null)
        {
            builder.WithCredentials(options.Username, options.Password ?? string.Empty);
        }

        if (options.UseTls)
        {
            builder.WithTlsOptions(tls => tls.UseTls().WithTargetHost(options.Host));
        }

        _options = builder.Build();
        _client = _factory.CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageAsync;
        _client.DisconnectedAsync += OnDisconnectedAsync;
    }

    public bool IsConnected => _client.IsConnected;

    public Func<MqttMessage, Task>? MessageReceived { get; set; }

    public Func<string, Task>? Disconnected { get; set; }

    public static IMqttTransport Create(MqttConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new MqttNetTransport(options);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        MqttClientConnectResult result;
        try
        {
            result = await _client.ConnectAsync(_options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is MqttCommunicationException or SocketException or IOException
            or AuthenticationException or TimeoutException or OperationCanceledException or MqttConfigurationException)
        {
            throw new MqttConnectException(MqttConnectFailure.Unreachable, Describe(exception), exception);
        }

        if (result.ResultCode != MqttClientConnectResultCode.Success)
        {
            throw Map(result.ResultCode);
        }

        // A drop between CONNACK and here would have been ignored by OnDisconnectedAsync: check once more.
        Interlocked.Exchange(ref _connected, 1);
        if (!_client.IsConnected && Interlocked.Exchange(ref _connected, 0) == 1)
        {
            throw new MqttConnectException(MqttConnectFailure.Unreachable, "the connection dropped right after connecting");
        }
    }

    public async Task SubscribeAsync(IReadOnlyCollection<string> topicFilters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topicFilters);
        if (topicFilters.Count == 0)
        {
            return;
        }

        // Brokers limit the filters per SUBSCRIBE packet loosely; batches of 64 stay far below any limit.
        foreach (var chunk in topicFilters.Chunk(64))
        {
            var builder = _factory.CreateSubscribeOptionsBuilder();
            foreach (var filter in chunk)
            {
                builder.WithTopicFilter(f => f.WithTopic(filter).WithAtLeastOnceQoS());
            }

            await _client.SubscribeAsync(builder.Build(), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();
        await _client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (_client.IsConnected)
        {
            Interlocked.Exchange(ref _connected, 0);
            try
            {
                await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), cancellationToken).ConfigureAwait(false);
            }
            catch (MqttCommunicationException)
            {
                // Already gone.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        MessageReceived = null;
        Disconnected = null;
        try
        {
            await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or OperationCanceledException)
        {
            // Best effort.
        }

        _client.Dispose();
    }

    private async Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        if (MessageReceived is { } handler)
        {
            var message = args.ApplicationMessage;
            await handler(new MqttMessage(message.Topic, message.Payload.ToArray(), message.Retain)).ConfigureAwait(false);
        }
    }

    private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        // Only report the loss of an established connection (failed attempts surface from ConnectAsync).
        if (Interlocked.Exchange(ref _connected, 0) == 1 && Disconnected is { } handler)
        {
            var reason = args.Exception is { } exception ? Describe(exception) : args.Reason.ToString();
            await handler(reason).ConfigureAwait(false);
        }
    }

    /// <summary>A CONNACK refusal (MQTTnet v5 returns it as the connect result instead of throwing).</summary>
    private static MqttConnectException Map(MqttClientConnectResultCode code) => code switch
    {
        MqttClientConnectResultCode.BadUserNameOrPassword or MqttClientConnectResultCode.BadAuthenticationMethod =>
            new MqttConnectException(MqttConnectFailure.BadCredentials, "The broker rejected the user name or password."),
        MqttClientConnectResultCode.NotAuthorized or MqttClientConnectResultCode.Banned =>
            new MqttConnectException(MqttConnectFailure.NotAuthorized, "The broker does not allow this user to connect."),
        MqttClientConnectResultCode.ServerUnavailable or MqttClientConnectResultCode.ServerBusy
            or MqttClientConnectResultCode.ConnectionRateExceeded or MqttClientConnectResultCode.QuotaExceeded =>
            new MqttConnectException(MqttConnectFailure.Unreachable, $"The broker is unavailable ({code})."),
        _ => new MqttConnectException(MqttConnectFailure.Refused, $"The broker refused the connection ({code})."),
    };

    /// <summary>A short description of a network failure: the exception type and, for sockets, the error code.</summary>
    private static string Describe(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case SocketException socket:
                    return $"network error ({socket.SocketErrorCode})";
                case AuthenticationException:
                    return "TLS handshake failed";
                case TimeoutException or MqttCommunicationTimedOutException:
                    return "timed out";
            }
        }

        return exception.GetType().Name;
    }
}

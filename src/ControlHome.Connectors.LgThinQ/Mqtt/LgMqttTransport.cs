using System.Buffers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MQTTnet;
using MQTTnet.Exceptions;
using MQTTnet.Formatter;

namespace ControlHome.Connectors.LgThinQ.Mqtt;

/// <summary>One received MQTT message.</summary>
internal sealed record LgMqttMessage(string Topic, ReadOnlyMemory<byte> Payload)
{
    public string Text => Encoding.UTF8.GetString(Payload.Span);

    public static LgMqttMessage FromText(string topic, string payload) => new(topic, Encoding.UTF8.GetBytes(payload));
}

/// <summary>How to reach LG's broker (from <c>GET route</c>) and who connects (the integration's client id and certificate).</summary>
/// <param name="TrustedRoots">The roots the broker's certificate must chain to (Amazon Root CA 1; a test CA in tests).</param>
internal sealed record LgMqttConnection(
    string Host,
    int Port,
    string ClientId,
    X509Certificate2 ClientCertificate,
    X509Certificate2Collection TrustedRoots,
    TimeSpan Timeout,
    TimeSpan KeepAlive)
{
    /// <summary>Prints without the certificate.</summary>
    public override string ToString() => $"{Host}:{Port} (client {ClientId})";
}

/// <summary>Why a connection attempt failed.</summary>
internal enum LgMqttFailure
{
    /// <summary>DNS, TCP or a timeout: retry later.</summary>
    Unreachable,

    /// <summary>The TLS handshake failed (the broker refused the client certificate, or its own was not trusted).</summary>
    TlsRejected,

    /// <summary>The broker refused the connection (CONNACK): the certificate is not (or no longer) authorized.</summary>
    NotAuthorized,
}

internal sealed class LgMqttConnectException(LgMqttFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public LgMqttFailure Failure { get; } = failure;
}

/// <summary>One broker connection (not reconnecting by itself: the runtime creates a new one per attempt).</summary>
internal interface ILgMqttTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>Called for every received message, in order (keep it short).</summary>
    Func<LgMqttMessage, Task>? MessageReceived { get; set; }

    /// <summary>Called once when an established connection drops, with a short non-secret reason.</summary>
    Func<string, Task>? Disconnected { get; set; }

    /// <exception cref="LgMqttConnectException">The broker cannot be reached or refused the connection.</exception>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Subscribes to a topic at QoS 1.</summary>
    Task SubscribeAsync(string topic, CancellationToken cancellationToken);
}

internal delegate ILgMqttTransport LgMqttTransportFactory(LgMqttConnection connection);

/// <summary>
/// <see cref="ILgMqttTransport"/> over MQTTnet v5 like LG's SDK: MQTT 3.1.1 over TLS (client certificate, broker pinned to
/// the trusted roots), the integration's client id, <c>CleanSession=false</c> (the broker keeps the subscription and QoS 1
/// messages across short drops), QoS 1.
/// </summary>
internal sealed class LgMqttNetTransport : ILgMqttTransport
{
    private static readonly MqttClientFactory _factory = new();

    private readonly IMqttClient _client;
    private readonly MqttClientOptions _options;
    private int _connected;

    private LgMqttNetTransport(LgMqttConnection connection)
    {
        var roots = connection.TrustedRoots;
        _options = new MqttClientOptionsBuilder()
            .WithTcpServer(connection.Host, connection.Port)
            .WithClientId(connection.ClientId)
            .WithCleanSession(false)
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithKeepAlivePeriod(connection.KeepAlive)
            .WithTimeout(connection.Timeout)
            .WithTlsOptions(tls => tls
                .UseTls()
                .WithTargetHost(connection.Host)
                .WithSslProtocols(SslProtocols.Tls12 | SslProtocols.Tls13)
                .WithClientCertificates([connection.ClientCertificate])
                .WithCertificateValidationHandler(args => LgTrust.Validate(args.Certificate, args.Chain, args.SslPolicyErrors, roots)))
            .Build();
        _client = _factory.CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageAsync;
        _client.DisconnectedAsync += OnDisconnectedAsync;
    }

    public bool IsConnected => _client.IsConnected;

    public Func<LgMqttMessage, Task>? MessageReceived { get; set; }

    public Func<string, Task>? Disconnected { get; set; }

    public static ILgMqttTransport Create(LgMqttConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new LgMqttNetTransport(connection);
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
            throw new LgMqttConnectException(IsTls(exception) ? LgMqttFailure.TlsRejected : LgMqttFailure.Unreachable, Describe(exception), exception);
        }

        if (result.ResultCode != MqttClientConnectResultCode.Success)
        {
            throw result.ResultCode is MqttClientConnectResultCode.ServerUnavailable or MqttClientConnectResultCode.ServerBusy
                or MqttClientConnectResultCode.QuotaExceeded or MqttClientConnectResultCode.ConnectionRateExceeded
                ? new LgMqttConnectException(LgMqttFailure.Unreachable, $"The broker is unavailable ({result.ResultCode}).")
                : new LgMqttConnectException(LgMqttFailure.NotAuthorized, $"The broker refused the connection ({result.ResultCode}).");
        }

        Interlocked.Exchange(ref _connected, 1);
        if (!_client.IsConnected && Interlocked.Exchange(ref _connected, 0) == 1)
        {
            throw new LgMqttConnectException(LgMqttFailure.Unreachable, "the connection dropped right after connecting");
        }
    }

    public async Task SubscribeAsync(string topic, CancellationToken cancellationToken)
    {
        var options = _factory.CreateSubscribeOptionsBuilder().WithTopicFilter(f => f.WithTopic(topic).WithAtLeastOnceQoS()).Build();
        var result = await _client.SubscribeAsync(options, cancellationToken).ConfigureAwait(false);
        if (result.Items.Any(i => i.ResultCode is not (MqttClientSubscribeResultCode.GrantedQoS0 or MqttClientSubscribeResultCode.GrantedQoS1
            or MqttClientSubscribeResultCode.GrantedQoS2)))
        {
            throw new LgMqttConnectException(LgMqttFailure.NotAuthorized, "The broker refused the subscription.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        MessageReceived = null;
        Disconnected = null;
        if (_client.IsConnected)
        {
            Interlocked.Exchange(ref _connected, 0);
            try
            {
                await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is MqttCommunicationException or ObjectDisposedException or OperationCanceledException)
            {
                // Already gone.
            }
        }

        _client.Dispose();
    }

    private async Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        if (MessageReceived is { } handler)
        {
            var message = args.ApplicationMessage;
            await handler(new LgMqttMessage(message.Topic, message.Payload.ToArray())).ConfigureAwait(false);
        }
    }

    private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        if (Interlocked.Exchange(ref _connected, 0) == 1 && Disconnected is { } handler)
        {
            await handler(args.Exception is { } exception ? Describe(exception) : args.Reason.ToString()).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether the failure looks like the broker refusing the TLS handshake (a rejected client certificate surfaces as an
    /// authentication error, or with TLS 1.3 as the connection being reset right after the handshake) rather than the
    /// network (DNS, refused TCP, timeouts).
    /// </summary>
    private static bool IsTls(Exception exception)
    {
        var tls = false;
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown }:
                    return tls;
                case SocketException or TimeoutException or MqttCommunicationTimedOutException:
                    return false;
                case AuthenticationException or IOException:
                    tls = true;
                    break;
            }
        }

        return tls;
    }

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

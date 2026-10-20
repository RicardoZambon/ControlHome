using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using ControlHome.Connectors.Mqtt.Transport;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace ControlHome.Connectors.Tests.Mqtt;

/// <summary>
/// One Mosquitto 2.1.2 broker for the MQTT integration tests (Testcontainers): <c>allow_anonymous false</c>, a password
/// file generated at start with a random password, no persistence (a restart forgets retained messages, like a fresh
/// broker). The host port is fixed for the container's lifetime, so a restart keeps the address the runtime uses.
/// </summary>
public sealed class MosquittoFixture : IAsyncLifetime
{
    /// <summary>Mosquitto 2.1.2: Docker Hub has no plain <c>2.1.2</c> tag, only <c>2.1.2-alpine</c> (also what Umbrel ships).</summary>
    public const string Image = "eclipse-mosquitto:2.1.2-alpine";
    public const string User = "controlhome";

    private static readonly TimeSpan _ready = TimeSpan.FromSeconds(30);
    private IContainer? _container;

    /// <summary>Random per run; never stored anywhere.</summary>
    public string Password { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public string Host => _container?.Hostname ?? throw new InvalidOperationException("The broker is not started.");

    public int Port { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var script = string.Join(
            " && ",
            "printf 'listener 1883\\nallow_anonymous false\\npassword_file /mosquitto/config/passwd\\npersistence false\\n' > /mosquitto/config/test.conf",

            // A restart runs the script again, and mosquitto_passwd -c refuses to overwrite an existing file.
            "rm -f /mosquitto/config/passwd",
            $"mosquitto_passwd -b -c /mosquitto/config/passwd {User} {Password}",
            "chown mosquitto:mosquitto /mosquitto/config/passwd",
            "chmod 0600 /mosquitto/config/passwd",
            "exec mosquitto -c /mosquitto/config/test.conf");

        for (var attempt = 1; ; attempt++)
        {
            Port = RandomNumberGenerator.GetInt32(20_000, 40_000);
            var container = new ContainerBuilder(Image)
                .WithPortBinding(Port, 1883)
                .WithEntrypoint("sh", "-c")
                .WithCommand(script)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("mosquitto version .* running"))
                .Build();
            try
            {
                await container.StartAsync();
                _container = container;
                break;
            }
            catch (Exception) when (attempt < 5)
            {
                // Most likely the random host port is taken: try another one.
                await container.DisposeAsync();
            }
        }

        await WaitUntilAcceptingAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Stops and starts the broker container (clients are disconnected; retained messages are lost).</summary>
    public async Task RestartAsync()
    {
        var container = _container ?? throw new InvalidOperationException("The broker is not started.");
        await container.StopAsync();
        await container.StartAsync();
        await WaitUntilAcceptingAsync();
    }

    internal MqttConnectionOptions Options(string clientId, string? secret = null) =>
        new(Host, Port, false, User, secret ?? Password, clientId, TimeSpan.FromSeconds(5));

    /// <summary>A plain client (publisher / observer) connected to the broker.</summary>
    public async Task<MqttTestClient> ClientAsync(params string[] subscriptions)
    {
        var client = new MqttTestClient(MqttNetTransport.Create(Options("test-" + Guid.NewGuid().ToString("N")[..8])));
        await client.ConnectAsync(subscriptions);
        return client;
    }

    private async Task WaitUntilAcceptingAsync()
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            await using var probe = MqttNetTransport.Create(Options("probe-" + Guid.NewGuid().ToString("N")[..8]));
            try
            {
                await probe.ConnectAsync(CancellationToken.None);
                return;
            }
            catch (MqttConnectException) when (watch.Elapsed < _ready)
            {
                await Task.Delay(200);
            }
        }
    }
}

[CollectionDefinition(Name)]
public sealed class MosquittoCollection : ICollectionFixture<MosquittoFixture>
{
    public const string Name = "Mosquitto";
}

/// <summary>A test-side MQTT client recording what it receives.</summary>
public sealed class MqttTestClient : IAsyncDisposable
{
    private readonly IMqttTransport _transport;
    private readonly ConcurrentQueue<MqttMessage> _received = new();

    internal MqttTestClient(IMqttTransport transport)
    {
        _transport = transport;
        _transport.MessageReceived = message =>
        {
            _received.Enqueue(message);
            return Task.CompletedTask;
        };
    }

    internal IReadOnlyList<MqttMessage> Received => [.. _received];

    public Task PublishAsync(string topic, string payload, bool retain = false) =>
        _transport.PublishAsync(topic, payload, retain, TestContext.Current.CancellationToken);

    /// <summary>Waits for a received message on <paramref name="topic"/> and returns its payload.</summary>
    public async Task<string> WaitForAsync(string topic, Func<string, bool>? match = null)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (_received.FirstOrDefault(m => m.Topic == topic && (match is null || match(m.Text))) is { } found)
            {
                return found.Text;
            }

            if (watch.Elapsed > TimeSpan.FromSeconds(15))
            {
                throw new TimeoutException($"No message on {topic}; got: {string.Join(", ", _received.Select(m => m.Topic))}");
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    public ValueTask DisposeAsync() => _transport.DisposeAsync();

    internal async Task ConnectAsync(string[] subscriptions)
    {
        await _transport.ConnectAsync(TestContext.Current.CancellationToken);
        await _transport.SubscribeAsync(subscriptions, TestContext.Current.CancellationToken);
    }
}

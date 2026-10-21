using ControlHome.Connectors.Mqtt;
using ControlHome.Connectors.Mqtt.Model;
using ControlHome.Connectors.Mqtt.Transport;

namespace ControlHome.Connectors.Tests.Mqtt;

/// <summary>
/// An in-memory MQTT broker for runtime tests: retained messages (an empty payload deletes one), topic-filter routing,
/// credentials, and a stop/start switch that drops every client and (like a broker without persistence) forgets the
/// retained messages. Live messages reach subscribers with the retain flag cleared, as MQTT 3.1.1 brokers do.
/// </summary>
internal sealed class FakeBroker
{
    private readonly Lock _lock = new();
    private readonly List<FakeTransport> _clients = [];
    private readonly List<(string Topic, MqttMessage Message)> _retained = [];
    private readonly List<(string ClientId, MqttPublish Message)> _published = [];
    private readonly List<MqttConnectionOptions> _attempts = [];

    /// <summary>Credentials the broker requires (both null: anonymous allowed).</summary>
    public (string User, string Password)? Credentials { get; set; }

    public bool IsUp { get; private set; } = true;

    public MqttTransportFactory Factory => options => new FakeTransport(this, options);

    /// <summary>Every connection attempt (with the options used).</summary>
    public IReadOnlyList<MqttConnectionOptions> Attempts
    {
        get
        {
            lock (_lock)
            {
                return [.. _attempts];
            }
        }
    }

    /// <summary>Messages the clients published, in order.</summary>
    public IReadOnlyList<MqttPublish> Published
    {
        get
        {
            lock (_lock)
            {
                return [.. _published.Select(p => p.Message)];
            }
        }
    }

    public IReadOnlyList<FakeTransport> Clients
    {
        get
        {
            lock (_lock)
            {
                return [.. _clients];
            }
        }
    }

    /// <summary>Publishes as a device would (retained messages are stored first, in order).</summary>
    public async Task PublishAsync(string topic, string payload, bool retain = false)
    {
        List<FakeTransport> targets;
        lock (_lock)
        {
            if (retain)
            {
                _retained.RemoveAll(r => r.Topic == topic);
                if (payload.Length > 0)
                {
                    _retained.Add((topic, MqttMessage.FromText(topic, payload, retain: true)));
                }
            }

            targets = [.. _clients.Where(c => c.IsSubscribedTo(topic))];
        }

        foreach (var target in targets)
        {
            await target.DeliverAsync(MqttMessage.FromText(topic, payload, retain: false));
        }
    }

    /// <summary>Drops every client; without persistence the retained messages are gone too.</summary>
    public async Task StopAsync(bool keepRetained = false)
    {
        List<FakeTransport> clients;
        lock (_lock)
        {
            IsUp = false;
            clients = [.. _clients];
            _clients.Clear();
            if (!keepRetained)
            {
                _retained.Clear();
            }
        }

        foreach (var client in clients)
        {
            await client.DropAsync("broker stopped");
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            IsUp = true;
        }
    }

    public IReadOnlyList<MqttPublish> PublishedTo(string topic) => [.. Published.Where(p => p.Topic == topic)];

    internal void Connect(FakeTransport client, MqttConnectionOptions options)
    {
        lock (_lock)
        {
            _attempts.Add(options);
            if (!IsUp)
            {
                throw new MqttConnectException(MqttConnectFailure.Unreachable, "network error (ConnectionRefused)");
            }

            if (Credentials is { } required && (options.Username != required.User || options.Password != required.Password))
            {
                throw new MqttConnectException(MqttConnectFailure.BadCredentials, "The broker rejected the user name or password.");
            }

            _clients.Add(client);
        }
    }

    internal void Disconnect(FakeTransport client)
    {
        lock (_lock)
        {
            _clients.Remove(client);
        }
    }

    internal List<MqttMessage> RetainedFor(IEnumerable<string> filters)
    {
        lock (_lock)
        {
            var list = filters.ToList();
            return [.. _retained.Where(r => list.Any(f => TopicFilter.Matches(f, r.Topic))).Select(r => r.Message)];
        }
    }

    internal async Task RouteAsync(FakeTransport sender, string topic, string payload, bool retain)
    {
        lock (_lock)
        {
            _published.Add((sender.ClientId, new MqttPublish(topic, payload, retain)));
        }

        await PublishAsync(topic, payload, retain);
    }
}

/// <summary>One client of a <see cref="FakeBroker"/>.</summary>
internal sealed class FakeTransport(FakeBroker broker, MqttConnectionOptions options) : IMqttTransport
{
    private readonly Lock _lock = new();
    private readonly List<string> _filters = [];
    private bool _connected;

    public string ClientId => options.ClientId;

    public bool IsConnected
    {
        get
        {
            lock (_lock)
            {
                return _connected;
            }
        }
    }

    public IReadOnlyList<string> Filters
    {
        get
        {
            lock (_lock)
            {
                return [.. _filters];
            }
        }
    }

    public Func<MqttMessage, Task>? MessageReceived { get; set; }

    public Func<string, Task>? Disconnected { get; set; }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        broker.Connect(this, options);
        lock (_lock)
        {
            _connected = true;
        }

        return Task.CompletedTask;
    }

    public async Task SubscribeAsync(IReadOnlyCollection<string> topicFilters, CancellationToken cancellationToken)
    {
        // Like Mosquitto: a malformed topic filter is a protocol error that drops the connection.
        if (topicFilters.FirstOrDefault(f => !IsValidFilter(f)) is { } malformed)
        {
            broker.Disconnect(this);
            await DropAsync($"malformed subscription '{malformed}'");
            throw new InvalidOperationException($"The broker dropped the connection (malformed subscription '{malformed}').");
        }

        lock (_lock)
        {
            if (!_connected)
            {
                throw new InvalidOperationException("Not connected.");
            }

            _filters.AddRange(topicFilters);
        }

        foreach (var retained in broker.RetainedFor(topicFilters))
        {
            await DeliverAsync(retained);
        }
    }

    public Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("Not connected.");
        }

        // Like MQTTnet: a topic name with wildcards is refused before anything is sent.
        if (topic.Length == 0 || topic.AsSpan().IndexOfAny('+', '#', '\0') >= 0)
        {
            throw new ArgumentException($"'{topic}' is not a valid topic name.", nameof(topic));
        }

        return broker.RouteAsync(this, topic, payload, retain);
    }

    /// <summary>MQTT 3.1.1 §4.7: non-empty, no NUL, <c>+</c> alone in its level, <c>#</c> alone in the last level.</summary>
    public static bool IsValidFilter(string filter)
    {
        if (filter.Length == 0 || filter.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        var levels = filter.Split('/');
        for (var i = 0; i < levels.Length; i++)
        {
            var level = levels[i];
            if ((level.Contains('+', StringComparison.Ordinal) && level != "+")
                || (level.Contains('#', StringComparison.Ordinal) && (level != "#" || i != levels.Length - 1)))
            {
                return false;
            }
        }

        return true;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _connected = false;
        }

        broker.Disconnect(this);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync(CancellationToken.None);

    public bool IsSubscribedTo(string topic)
    {
        lock (_lock)
        {
            return _connected && _filters.Any(f => TopicFilter.Matches(f, topic));
        }
    }

    public async Task DeliverAsync(MqttMessage message)
    {
        if (IsConnected && MessageReceived is { } handler)
        {
            await handler(message);
        }
    }

    public async Task DropAsync(string reason)
    {
        lock (_lock)
        {
            _connected = false;
            _filters.Clear();
        }

        if (Disconnected is { } handler)
        {
            await handler(reason);
        }
    }
}

using System.Threading.Channels;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.Mqtt.HomeAssistant;
using ControlHome.Connectors.Mqtt.Model;
using ControlHome.Connectors.Mqtt.Tasmota;
using ControlHome.Connectors.Mqtt.Transport;
using ControlHome.Connectors.Mqtt.Zigbee2Mqtt;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ControlHome.Connectors.Mqtt;

/// <summary>
/// One MQTT integration: a broker connection with its own reconnect loop (exponential back-off with jitter; MQTTnet v5 has
/// no managed client) that re-subscribes every adapter filter and device topic after each reconnect. Messages are queued
/// and handled in order on one loop: adapters turn discovery into devices, then the message is routed to the devices that
/// listen on its topic. Retained messages that arrive before their device is known are kept and replayed once it is.
/// While the broker is unreachable every device is reported <see cref="Availability.Unknown"/>. A bad message is logged and
/// skipped; a device the host cannot save fails the run (the supervisor restarts it, and the retained discovery is replayed).
/// </summary>
internal sealed partial class MqttRuntime : IConnectorRuntime, IMqttAdapterHost
{
    /// <summary>Retained messages kept for topics no device claims yet (bounded).</summary>
    internal const int MaxUnclaimedRetained = 10_000;

    private const int InboxCapacity = 10_000;

    private readonly IntegrationInstance _instance;
    private readonly MqttSettings _settings;
    private readonly MqttTransportFactory _transports;
    private readonly MqttRuntimeOptions _options;
    private readonly ReconnectBackoff _backoff;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, MqttDevice> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _byTopic = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _upserted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _refreshed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Availability> _availability = new(StringComparer.Ordinal);
    private readonly HashSet<string> _explicitAvailability = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MqttMessage> _unclaimed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _subscribed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _usedKeys = new(StringComparer.Ordinal);
    private IReadOnlyList<IMqttAdapter> _adapters = [];
    private IReadOnlyList<string> _filters = [];
    private IConnectorContext? _context;
    private IMqttTransport? _transport;
    private long _connections;

    internal MqttRuntime(IntegrationInstance instance, MqttSettings settings, MqttTransportFactory transports, MqttRuntimeOptions options)
    {
        _instance = instance;
        _settings = settings;
        _transports = transports;
        _options = options;
        _backoff = new ReconnectBackoff(options.ReconnectInitial, options.ReconnectMax, options.ReconnectJitter);
    }

    public MqttSettings Settings => _settings;

    /// <summary>Successful connections so far (tests wait on it after a broker restart).</summary>
    internal long Connections => Interlocked.Read(ref _connections);

    /// <summary>Every topic subscribed on the current connection.</summary>
    internal IReadOnlyCollection<string> Subscriptions
    {
        get
        {
            lock (_lock)
            {
                return [.. _subscribed];
            }
        }
    }

    public ILogger Logger => _context?.Logger ?? NullLogger.Instance;

    public async Task RunAsync(IConnectorContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_lock)
        {
            if (_context is not null)
            {
                throw new InvalidOperationException("This MQTT integration is already running.");
            }

            _context = context;
        }

        var inbox = Channel.CreateBounded<MqttMessage>(new BoundedChannelOptions(InboxCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? processing = null;
        Task? connecting = null;
        try
        {
            context.SetStatus(IntegrationStatus.Connecting, null);
            var secret = await _instance.Secrets.GetAsync(MqttSettings.PasswordField, cancellationToken).ConfigureAwait(false);
            var connection = new MqttConnectionOptions(
                _settings.Host,
                _settings.Port,
                _settings.UseTls,
                _settings.Username,
                secret,
                _settings.EffectiveClientId(_instance.Id),
                _options.ConnectTimeout);
            CreateAdapters();
            processing = ProcessAsync(context, inbox.Reader, stop.Token);
            connecting = ConnectLoopAsync(context, connection, inbox.Writer, stop.Token);

            // Both loops run until stopped. The message loop ends early only when the inventory cannot be saved: that
            // fails the run, so the supervisor restarts it and the broker replays the retained discovery messages.
            var first = await Task.WhenAny(processing, connecting).ConfigureAwait(false);
            await stop.CancelAsync().ConfigureAwait(false);
            await first.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped.
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            inbox.Writer.TryComplete();
            await StoppedAsync(processing).ConfigureAwait(false);
            await StoppedAsync(connecting).ConfigureAwait(false);
            lock (_lock)
            {
                _context = null;
                _transport = null;
                _subscribed.Clear();
            }
        }
    }

    public async Task<CommandOutcome> ExecuteAsync(DeviceCommandBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        IConnectorContext context;
        MqttDevice? device;
        lock (_lock)
        {
            if (_context is null)
            {
                return CommandOutcome.Reject(CommandRejectionCodes.Unavailable, "The MQTT integration is not running.");
            }

            context = _context;
            device = _devices.GetValueOrDefault(batch.ExternalDeviceId);
        }

        if (device is null)
        {
            return CommandOutcome.Reject(CommandRejectionCodes.UnknownDevice, "No such MQTT device.");
        }

        var (plan, rejection, safetyCritical) = device.Plan(batch);
        if (rejection is not null)
        {
            return rejection;
        }

        IMqttTransport transport;
        lock (_lock)
        {
            if (_transport is not { IsConnected: true } connected)
            {
                return CommandOutcome.Reject(CommandRejectionCodes.Unavailable, "The MQTT broker is not connected.");
            }

            transport = connected;
            var now = context.Time.GetUtcNow();
            foreach (var expired in _usedKeys.Where(k => now - k.Value > _options.IdempotencyWindow).Select(k => k.Key).ToList())
            {
                _usedKeys.Remove(expired);
            }

            if (string.IsNullOrEmpty(batch.IdempotencyKey))
            {
                if (safetyCritical)
                {
                    return CommandOutcome.Reject(CommandRejectionCodes.IdempotencyRequired, "A safety-critical command needs an idempotency key.");
                }
            }
            else if (!_usedKeys.TryAdd(batch.IdempotencyKey, now))
            {
                return CommandOutcome.Reject(CommandRejectionCodes.Duplicate, "This command was already executed.");
            }
        }

        // At-most-once: the key stays used even if publishing fails now (never retried).
        try
        {
            foreach (var publish in plan!.Publishes)
            {
                await transport.PublishAsync(publish.Topic, publish.Payload, publish.Retain, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogPublishFailed(context.Logger, exception, batch.ExternalDeviceId);
            return CommandOutcome.Reject(CommandRejectionCodes.Unavailable, "The MQTT broker connection dropped.");
        }

        Report(device, plan.Optimistic);
        return plan.CompletesOnPublish ? CommandOutcome.CompletedOutcome : CommandOutcome.AcceptedOutcome;
    }

    public async Task RefreshAsync(string externalDeviceId, CancellationToken cancellationToken)
    {
        if (FindDevice(externalDeviceId) is { } device)
        {
            foreach (var publish in device.Refresh())
            {
                await PublishAsync(publish, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Waits for a loop that was told to stop; the first loop to end has already been awaited by the run.</summary>
    private static async Task StoppedAsync(Task? loop)
    {
        if (loop is not null)
        {
            await loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    // ---- IMqttAdapterHost -----------------------------------------------------------------------------------------------

    public MqttDevice? FindDevice(string externalId)
    {
        lock (_lock)
        {
            return _devices.GetValueOrDefault(externalId);
        }
    }

    public async Task SetDeviceAsync(MqttDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        var context = _context ?? throw new InvalidOperationException("The MQTT integration is not running.");
        var id = device.ExternalId;
        var inventory = CapabilityJson.Serialize(device.Discovered);
        bool changed;
        lock (_lock)
        {
            changed = !_upserted.TryGetValue(id, out var last) || !string.Equals(last, inventory, StringComparison.Ordinal);
        }

        if (changed)
        {
            try
            {
                await context.UpsertDeviceAsync(device.Discovered, cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException exception)
            {
                // A mapping bug must not stop the integration: skip this device.
                LogUpsertRejected(context.Logger, exception, id);
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new MqttInventoryException(id, exception);
            }
        }

        List<string> subscribe;
        List<MqttMessage> replay = [];
        bool refresh;
        IMqttTransport? transport;
        lock (_lock)
        {
            _upserted[id] = inventory;
            if (_devices.TryGetValue(id, out var previous))
            {
                Unindex(previous);
            }

            _devices[id] = device;
            foreach (var topic in device.Topics)
            {
                if (!_byTopic.TryGetValue(topic, out var ids))
                {
                    ids = new HashSet<string>(StringComparer.Ordinal);
                    _byTopic[topic] = ids;
                }

                ids.Add(id);
                if (_unclaimed.Remove(topic, out var retained))
                {
                    replay.Add(retained);
                }
            }

            transport = _transport;
            subscribe = transport is null ? [] : [.. device.Topics.Where(t => !_subscribed.Contains(t) && !CoveredByFilter(t))];
            refresh = transport is not null && _refreshed.Add(id);
        }

        Report(device, device.InitialReadings);
        if (transport is not null && subscribe.Count > 0)
        {
            try
            {
                await transport.SubscribeAsync(subscribe, cancellationToken).ConfigureAwait(false);
                lock (_lock)
                {
                    _subscribed.UnionWith(subscribe);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The connection dropped: the next connection subscribes every device topic.
                LogSubscribeFailed(context.Logger, exception, id);
            }
        }

        foreach (var message in replay)
        {
            Report(device, device.Read(message));
        }

        if (refresh)
        {
            foreach (var publish in device.Refresh())
            {
                await PublishAsync(publish, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task RemoveDeviceAsync(string externalId, CancellationToken cancellationToken)
    {
        var context = _context ?? throw new InvalidOperationException("The MQTT integration is not running.");
        lock (_lock)
        {
            if (_devices.Remove(externalId, out var device))
            {
                Unindex(device);
            }

            _upserted.Remove(externalId);
            _refreshed.Remove(externalId);
            _availability.Remove(externalId);
            _explicitAvailability.Remove(externalId);
        }

        try
        {
            await context.RemoveDeviceAsync(externalId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new MqttInventoryException(externalId, exception);
        }
    }

    public void Report(MqttDevice device, IReadOnlyList<MqttReading> readings)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(readings);
        if (_context is not { } context || readings.Count == 0)
        {
            return;
        }

        var id = device.ExternalId;
        var reported = false;
        foreach (var reading in readings)
        {
            switch (reading)
            {
                case MqttReading.State state:
                    context.ReportState(new StateReport(id, state.Endpoint, state.Capability, state.Attribute, state.Value, null));
                    reported = true;
                    break;
                case MqttReading.Event @event:
                    context.ReportEvent(new EventReport(id, @event.Endpoint, @event.Capability, @event.Attribute, @event.Name, null, null));
                    reported = true;
                    break;
                case MqttReading.Reachability reachability:
                    lock (_lock)
                    {
                        _explicitAvailability.Add(id);
                    }

                    SetAvailability(context, id, reachability.Availability);
                    break;
            }
        }

        bool implicitOnline;
        lock (_lock)
        {
            implicitOnline = reported && !device.HasAvailabilityTopic && !_explicitAvailability.Contains(id);
        }

        if (implicitOnline)
        {
            SetAvailability(context, id, Availability.Online);
        }
    }

    public async Task<bool> PublishAsync(MqttPublish message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        IMqttTransport? transport;
        lock (_lock)
        {
            transport = _transport;
        }

        if (transport is null)
        {
            return false;
        }

        try
        {
            await transport.PublishAsync(message.Topic, message.Payload, message.Retain, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogPublishFailed(Logger, exception, message.Topic);
            return false;
        }
    }

    // ---- connection loop ------------------------------------------------------------------------------------------------

    private void CreateAdapters()
    {
        var adapters = new List<IMqttAdapter>();
        if (_settings.Zigbee2MqttBaseTopic is { } baseTopic)
        {
            adapters.Add(new Z2mAdapter(baseTopic, this));
        }

        if (_settings.DiscoveryPrefix is { } prefix)
        {
            adapters.Add(new HaDiscoveryAdapter(prefix, skipZigbee2Mqtt: _settings.Zigbee2MqttBaseTopic is not null, this));
        }

        if (_settings.TasmotaDiscovery)
        {
            adapters.Add(new TasmotaAdapter(this));
        }

        _adapters = adapters;
        _filters = [.. adapters.SelectMany(a => a.TopicFilters).Distinct(StringComparer.Ordinal)];
    }

    private async Task ConnectLoopAsync(IConnectorContext context, MqttConnectionOptions connection, ChannelWriter<MqttMessage> inbox, CancellationToken cancellationToken)
    {
        var failures = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transport = _transports(connection);
            var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.MessageReceived = message => inbox.WriteAsync(message, cancellationToken).AsTask();
            transport.Disconnected = reason =>
            {
                lost.TrySetResult(reason);
                return Task.CompletedTask;
            };

            var wasConnected = false;
            IntegrationStatus status;
            string message;
            try
            {
                await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
                wasConnected = true;
                List<string> topics;
                List<MqttDevice> known;
                lock (_lock)
                {
                    // Published before subscribing: devices discovered from the retained messages that follow subscribe
                    // their own topics and are asked for their state on this connection.
                    _subscribed.Clear();
                    topics = [.. _filters, .. _byTopic.Keys.Where(t => !CoveredByFilter(t))];
                    known = [.. _devices.Values];
                    _transport = transport;
                }

                await transport.SubscribeAsync(topics, cancellationToken).ConfigureAwait(false);
                lock (_lock)
                {
                    _subscribed.UnionWith(topics);
                }

                failures = 0;
                Interlocked.Increment(ref _connections);
                LogConnected(context.Logger, _settings.Endpoint, topics.Count);
                context.SetStatus(IntegrationStatus.Ok, null);
                await OnConnectedAsync(context, known, cancellationToken).ConfigureAwait(false);

                var reason = await WaitForLossAsync(context, transport, lost.Task, cancellationToken).ConfigureAwait(false);
                LogDisconnected(context.Logger, _settings.Endpoint, reason);
                status = IntegrationStatus.Degraded;
                message = $"Lost the connection to the MQTT broker at {_settings.Endpoint} ({reason}); reconnecting.";
            }
            catch (MqttConnectException exception)
            {
                LogConnectFailed(context.Logger, _settings.Endpoint, exception.Failure, exception.Message);
                (status, message) = exception.Failure switch
                {
                    MqttConnectFailure.BadCredentials =>
                        (IntegrationStatus.NeedsReauth, $"The MQTT broker at {_settings.Endpoint} rejected the user name or password."),
                    MqttConnectFailure.NotAuthorized =>
                        (IntegrationStatus.NeedsReauth, $"The MQTT broker at {_settings.Endpoint} does not allow this user to connect."),
                    MqttConnectFailure.Refused =>
                        (IntegrationStatus.Error, $"The MQTT broker at {_settings.Endpoint} refused the connection: {exception.Message}"),
                    _ => (IntegrationStatus.Degraded, $"Cannot reach the MQTT broker at {_settings.Endpoint} ({exception.Message}); retrying."),
                };
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is not OperationCanceledException)
            {
                // Subscribing failed (the connection dropped meanwhile) or a transport error: retry.
                LogConnectionError(context.Logger, exception, _settings.Endpoint);
                status = IntegrationStatus.Degraded;
                message = $"The connection to the MQTT broker at {_settings.Endpoint} failed; retrying.";
            }
            finally
            {
                lock (_lock)
                {
                    if (ReferenceEquals(_transport, transport))
                    {
                        _transport = null;
                    }

                    _subscribed.Clear();
                }

                transport.MessageReceived = null;
                transport.Disconnected = null;
                try
                {
                    await transport.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    LogConnectionError(context.Logger, exception, _settings.Endpoint);
                }
            }

            // The timer starts before the status is published, so whoever reacts to the status (a test clock) finds it armed.
            var retry = Task.Delay(_backoff.Next(failures++), context.Time, cancellationToken);
            if (wasConnected)
            {
                MarkAllUnknown(context);
            }

            context.SetStatus(status, message);
            await retry.ConfigureAwait(false);
        }
    }

    private async Task<string> WaitForLossAsync(IConnectorContext context, IMqttTransport transport, Task<string> lost, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var check = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(_options.ConnectionCheckInterval, context.Time, check.Token);
            var finished = await Task.WhenAny(lost, delay).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (finished == lost)
            {
                await check.CancelAsync().ConfigureAwait(false);
                return await lost.ConfigureAwait(false);
            }

            if (!transport.IsConnected)
            {
                return "connection check failed";
            }
        }
    }

    /// <param name="known">The devices known before this connection (devices discovered on it are asked when they are set).</param>
    private async Task OnConnectedAsync(IConnectorContext context, IReadOnlyList<MqttDevice> known, CancellationToken cancellationToken)
    {
        foreach (var adapter in _adapters)
        {
            await adapter.OnConnectedAsync(cancellationToken).ConfigureAwait(false);
        }

        // Devices that report no availability are unknown after a reconnect until they report: ask them now.
        List<MqttDevice> devices;
        lock (_lock)
        {
            devices = [.. known.Where(d => !d.HasAvailabilityTopic && !_explicitAvailability.Contains(d.ExternalId))];
            foreach (var device in devices)
            {
                _refreshed.Add(device.ExternalId);
            }
        }

        foreach (var device in devices)
        {
            foreach (var publish in device.Refresh())
            {
                await PublishAsync(publish, cancellationToken).ConfigureAwait(false);
            }
        }

        LogAdaptersReady(context.Logger, _adapters.Count, devices.Count);
    }

    private async Task ProcessAsync(IConnectorContext context, ChannelReader<MqttMessage> inbox, CancellationToken cancellationToken)
    {
        await foreach (var message in inbox.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await DispatchAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (MqttInventoryException)
            {
                // The device would be lost until its discovery is published again: fail the run instead (restarted).
                throw;
            }
            catch (Exception exception)
            {
                // One bad message never stops the integration.
                LogMessageFailed(context.Logger, exception, message.Topic);
            }
        }
    }

    private async Task DispatchAsync(MqttMessage message, CancellationToken cancellationToken)
    {
        var handled = false;
        foreach (var adapter in _adapters)
        {
            handled |= await adapter.HandleAsync(message, cancellationToken).ConfigureAwait(false);
        }

        List<MqttDevice> targets;
        lock (_lock)
        {
            targets = _byTopic.TryGetValue(message.Topic, out var ids)
                ? [.. ids.Select(id => _devices.GetValueOrDefault(id)).OfType<MqttDevice>()]
                : [];
            if (!handled && targets.Count == 0 && message.Retain)
            {
                if (message.IsEmpty)
                {
                    _unclaimed.Remove(message.Topic);
                }
                else if (_unclaimed.Count < MaxUnclaimedRetained || _unclaimed.ContainsKey(message.Topic))
                {
                    _unclaimed[message.Topic] = message;
                }
            }
        }

        foreach (var device in targets)
        {
            Report(device, device.Read(message));
        }
    }

    private void MarkAllUnknown(IConnectorContext context)
    {
        List<string> ids;
        lock (_lock)
        {
            ids = [.. _devices.Keys];
            _explicitAvailability.Clear();
        }

        foreach (var id in ids)
        {
            SetAvailability(context, id, Availability.Unknown);
        }
    }

    private void SetAvailability(IConnectorContext context, string id, Availability availability)
    {
        lock (_lock)
        {
            if (!_devices.ContainsKey(id) || (_availability.TryGetValue(id, out var current) && current == availability))
            {
                return;
            }

            _availability[id] = availability;
        }

        context.ReportAvailability(id, availability, null);
    }

    private void Unindex(MqttDevice device)
    {
        foreach (var topic in device.Topics)
        {
            if (_byTopic.TryGetValue(topic, out var ids) && ids.Remove(device.ExternalId) && ids.Count == 0)
            {
                _byTopic.Remove(topic);
            }
        }
    }

    /// <summary>Whether one of the adapters' wildcard filters already delivers <paramref name="topic"/>.</summary>
    private bool CoveredByFilter(string topic) => _filters.Any(filter => TopicFilter.Matches(filter, topic));

    [LoggerMessage(Level = LogLevel.Information, Message = "MQTT connected to {Endpoint}; subscribed to {Count} topics")]
    private static partial void LogConnected(ILogger logger, string endpoint, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "MQTT adapters ready ({Adapters}); asked {Devices} devices for their state")]
    private static partial void LogAdaptersReady(ILogger logger, int adapters, int devices);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MQTT lost the connection to {Endpoint}: {Reason}")]
    private static partial void LogDisconnected(ILogger logger, string endpoint, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MQTT could not connect to {Endpoint} ({Failure}): {Reason}")]
    private static partial void LogConnectFailed(ILogger logger, string endpoint, MqttConnectFailure failure, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MQTT connection to {Endpoint} failed")]
    private static partial void LogConnectionError(ILogger logger, Exception exception, string endpoint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MQTT could not handle a message on {Topic}")]
    private static partial void LogMessageFailed(ILogger logger, Exception exception, string topic);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MQTT could not publish for {Target}")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception, string target);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MQTT could not subscribe the topics of device {ExternalId}")]
    private static partial void LogSubscribeFailed(ILogger logger, Exception exception, string externalId);

    [LoggerMessage(Level = LogLevel.Error, Message = "MQTT device {ExternalId} was rejected by the inventory (skipped)")]
    private static partial void LogUpsertRejected(ILogger logger, Exception exception, string externalId);
}

/// <summary>The host could not save (or orphan) a device: the run fails so the supervisor restarts it.</summary>
internal sealed class MqttInventoryException(string externalId, Exception inner)
    : Exception($"The inventory could not be updated for MQTT device {externalId}.", inner);

/// <summary>MQTT topic filter matching (<c>+</c> one level, <c>#</c> the rest).</summary>
internal static class TopicFilter
{
    public static bool Matches(string filter, string topic)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(topic);
        var filterLevels = filter.Split('/');
        var topicLevels = topic.Split('/');
        for (var i = 0; i < filterLevels.Length; i++)
        {
            if (filterLevels[i] == "#")
            {
                return true;
            }

            if (i >= topicLevels.Length || (filterLevels[i] != "+" && filterLevels[i] != topicLevels[i]))
            {
                return false;
            }
        }

        return filterLevels.Length == topicLevels.Length;
    }
}

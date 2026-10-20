using System.Threading.Channels;
using ControlHome.Application.Devices;
using ControlHome.Application.Realtime;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlHome.Infrastructure.Devices;

/// <summary>
/// The state pipeline's single processing loop. Connector reports (<see cref="ReportState"/>, <see cref="ReportEvent"/>,
/// <see cref="ReportAvailability"/>) never block: they land in a coalescing queue — one slot per attribute, the newest
/// observation wins — bounded to <see cref="DeviceRuntimeOptions.QueueCapacity"/> distinct attributes (beyond that the oldest
/// waiting report is dropped), so a flooding connector can neither exhaust memory nor delay other devices for long.
/// For each report the loop, in order:
/// <list type="number">
/// <item>resolves the <c>AttributeId</c> (<see cref="DeviceRegistry"/>; an attribute of a known capability seen for the
/// first time is created through the inventory);</item>
/// <item>drops values the attribute's schema rejects, out-of-order reports (older than the cached observation) and
/// duplicates (<see cref="StateCache.Apply"/>);</item>
/// <item>updates the <see cref="StateCache"/>, pushes a <see cref="StateDelta"/> (batched, ≤ 50 ms), queues the write-behind
/// (current value, history row for recorded attributes and events, device last-seen);</item>
/// <item>hands the batch to every <see cref="IStateChangeSubscriber"/> (command confirmations; automations later).</item>
/// </list>
/// Momentary events are never coalesced or deduplicated. While a report waits, a newer one for the same attribute replaces
/// it, but an older observation never replaces a newer one (it counts as out of order).
/// <para>
/// The loop never waits on the database for long: an address the registry does not know is looked up through the
/// inventory at most once per <see cref="UnknownRetryAfter"/> (unknown) or per back-off window (the lookup failed or took
/// longer than <see cref="ResolveTimeout"/>), so an outage or a busy inventory cannot stall every other report.
/// </para>
/// </summary>
public sealed partial class StateProcessor : IAsyncDisposable
{
    private const int MaxBatch = 1_000;

    /// <summary>How long an address the inventory does not know is dropped without asking again.</summary>
    public static readonly TimeSpan UnknownRetryAfter = TimeSpan.FromMinutes(1);

    /// <summary>The longest the loop waits for the inventory to resolve an address (then it counts as a failed lookup).</summary>
    public static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// After a failed (or timed out) lookup the address is dropped for this long, doubling per failure in a row up to
    /// <see cref="UnknownRetryAfter"/>.
    /// </summary>
    public static readonly TimeSpan ResolveFailureBackoff = TimeSpan.FromSeconds(5);

    private readonly DeviceRegistry _registry;
    private readonly StateCache _cache;
    private readonly StateWriteBehind _writeBehind;
    private readonly IDeviceInventory _inventory;
    private readonly RealtimeBatcher _realtime;
    private readonly Func<IEnumerable<IStateChangeSubscriber>> _subscribers;
    private readonly RuntimeClock _clock;
    private readonly DeviceRuntimeOptions _options;
    private readonly ILogger<StateProcessor> _logger;

    private readonly Lock _lock = new();
    private readonly Dictionary<ChangeKey, PendingChange> _pending = [];
    private readonly Channel<ChangeKey> _keys;
    private readonly Dictionary<AttributeAddress, UnknownAddress> _unknown = [];
    private long _sequence;
    private long _received;
    private long _dropped;
    private long _coalesced;
    private long _outOfOrder;
    private long _processed;
    private int _busy;

    private IStateChangeSubscriber[] _activeSubscribers = [];
    private CancellationTokenSource? _stop;
    private Task _loop = Task.CompletedTask;

    public StateProcessor(
        DeviceRegistry registry,
        StateCache cache,
        StateWriteBehind writeBehind,
        IDeviceInventory inventory,
        RealtimeBatcher realtime,
        Func<IEnumerable<IStateChangeSubscriber>> subscribers,
        RuntimeClock clock,
        IOptions<DeviceRuntimeOptions> options,
        ILogger<StateProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _registry = registry;
        _cache = cache;
        _writeBehind = writeBehind;
        _inventory = inventory;
        _realtime = realtime;
        _subscribers = subscribers;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
        _keys = Channel.CreateBounded<ChangeKey>(
            new BoundedChannelOptions(Math.Max(1, _options.QueueCapacity))
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            },
            OnKeyDropped);
    }

    private enum ChangeKind
    {
        State,
        Event,
        Availability,
        Injected,
    }

    /// <summary>Reports accepted into the queue (since start).</summary>
    public long Received => Interlocked.Read(ref _received);

    /// <summary>Reports dropped: queue full, unknown attribute or invalid value.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Reports replaced by a newer one for the same attribute while waiting.</summary>
    public long Coalesced => Interlocked.Read(ref _coalesced);

    /// <summary>Reports older than the cached observation, or than the report waiting for the same attribute.</summary>
    public long OutOfOrder => Interlocked.Read(ref _outOfOrder);

    /// <summary>Reports taken off the queue and processed.</summary>
    public long Processed => Interlocked.Read(ref _processed);

    public bool IsRunning => _stop is not null;

    /// <summary>Queues a state report of an integration (thread-safe, never blocks).</summary>
    public void ReportState(Guid integrationId, StateReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(report.Value);
        var address = new AttributeAddress(integrationId, report.ExternalDeviceId, report.EndpointKey, report.CapabilityId, report.AttributeKey);
        Enqueue(
            new ChangeKey(ChangeKind.State, address, 0),
            new PendingChange(ChangeKind.State, address, 0, report.Value, report.ObservedAtUtc ?? _clock.Time.GetUtcNow(), default));
    }

    /// <summary>Queues a momentary event (never coalesced).</summary>
    public void ReportEvent(Guid integrationId, EventReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var address = new AttributeAddress(integrationId, report.ExternalDeviceId, report.EndpointKey, report.CapabilityId, report.AttributeKey);
        Enqueue(
            new ChangeKey(ChangeKind.Event, address, Interlocked.Increment(ref _sequence)),
            new PendingChange(
                ChangeKind.Event,
                address,
                0,
                StateValue.String(report.Event),
                report.OccurredAtUtc ?? _clock.Time.GetUtcNow(),
                default));
    }

    /// <summary>Queues a device's availability (the latest per device wins).</summary>
    public void ReportAvailability(Guid integrationId, string externalDeviceId, Availability availability)
    {
        ArgumentNullException.ThrowIfNull(externalDeviceId);
        var address = new AttributeAddress(integrationId, externalDeviceId, string.Empty, string.Empty, string.Empty);
        Enqueue(
            new ChangeKey(ChangeKind.Availability, address, 0),
            new PendingChange(ChangeKind.Availability, address, 0, StateValue.Null, _clock.Time.GetUtcNow(), availability));
    }

    /// <summary>Queues a value for a known attribute id, as if its connector had reported it (test hook).</summary>
    public void InjectState(int attributeId, StateValue value, DateTimeOffset? observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(value);
        var address = new AttributeAddress(Guid.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        Enqueue(
            new ChangeKey(ChangeKind.Injected, address, attributeId),
            new PendingChange(ChangeKind.Injected, address, attributeId, value, observedAtUtc ?? _clock.Time.GetUtcNow(), default));
    }

    /// <summary>Starts the processing loop (subscribers are resolved now).</summary>
    public void Start()
    {
        if (_stop is not null)
        {
            return;
        }

        _activeSubscribers = [.. _subscribers()];
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _loop = BackgroundWork.Run(() => RunAsync(token));
    }

    /// <summary>Stops the loop (reports still waiting are discarded) and flushes the client batcher.</summary>
    public async Task StopAsync()
    {
        if (_stop is not { } stop)
        {
            return;
        }

        await stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }

        stop.Dispose();
        _stop = null;
        Clear();
        await _realtime.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>Discards every waiting report.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _pending.Clear();
            _unknown.Clear();

            // Drained under the lock (Enqueue adds its pending entry under it too): a pending entry whose key was drained
            // would never be read again, and every later report for it would only coalesce into it.
            while (_keys.Reader.TryRead(out _))
            {
                // Drain.
            }
        }
    }

    /// <summary>Waits until every report queued so far has been processed (tests).</summary>
    public async Task WhenIdleAsync(TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        while (true)
        {
            bool idle;
            lock (_lock)
            {
                idle = _pending.Count == 0 && Volatile.Read(ref _busy) == 0;
            }

            if (idle)
            {
                return;
            }

            await Task.Delay(5, deadline.Token).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void Enqueue(ChangeKey key, PendingChange change)
    {
        Interlocked.Increment(ref _received);
        lock (_lock)
        {
            if (_pending.TryGetValue(key, out var waiting))
            {
                if (key.Kind is ChangeKind.State or ChangeKind.Injected && change.ObservedAtUtc < waiting.ObservedAtUtc)
                {
                    // Older than the report already waiting: the newer observation stays (as the cache would decide).
                    Interlocked.Increment(ref _outOfOrder);
                    return;
                }

                _pending[key] = change;
                Interlocked.Increment(ref _coalesced);
                return;
            }

            _pending[key] = change;
        }

        // Outside the lock: a full channel drops its oldest key and calls OnKeyDropped (which takes the lock).
        _keys.Writer.TryWrite(key);
    }

    private void OnKeyDropped(ChangeKey key)
    {
        lock (_lock)
        {
            _pending.Remove(key);
        }

        Interlocked.Increment(ref _dropped);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var reader = _keys.Reader;
        var batch = new List<PendingChange>(MaxBatch);
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Exchange(ref _busy, 1);
            try
            {
                batch.Clear();
                while (batch.Count < MaxBatch && reader.TryRead(out var key))
                {
                    lock (_lock)
                    {
                        if (_pending.Remove(key, out var change))
                        {
                            batch.Add(change);
                        }
                    }
                }

                if (batch.Count > 0)
                {
                    await ProcessAsync(batch, cancellationToken).ConfigureAwait(false);
                    Interlocked.Add(ref _processed, batch.Count);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogBatchFailed(ex);
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }
    }

    private async Task ProcessAsync(List<PendingChange> batch, CancellationToken cancellationToken)
    {
        var changes = new List<AttributeStateChange>();
        foreach (var change in batch)
        {
            if (change.Kind == ChangeKind.Availability)
            {
                ApplyAvailability(change);
                continue;
            }

            var attribute = await ResolveAsync(change, cancellationToken).ConfigureAwait(false);
            if (attribute is null)
            {
                Interlocked.Increment(ref _dropped);
                continue;
            }

            var isEvent = change.Kind == ChangeKind.Event;
            if (!isEvent
                && change.Value.Kind != StateValueKind.Null
                && attribute.Definition is { } definition
                && !ValueSchemaValidator.Accepts(definition.Schema, change.Value))
            {
                Interlocked.Increment(ref _dropped);
                LogInvalidValue(attribute.AttributeId, attribute.Address.Capability, attribute.Address.Attribute);
                continue;
            }

            var result = _cache.Apply(attribute.AttributeId, change.Value, change.ObservedAtUtc, isEvent);
            switch (result.Outcome)
            {
                case StateApplyOutcome.OutOfOrder:
                    Interlocked.Increment(ref _outOfOrder);
                    continue;
                case StateApplyOutcome.Unchanged:
                    changes.Add(new AttributeStateChange(
                        attribute.AttributeId,
                        result.Previous?.Value,
                        change.Value,
                        change.ObservedAtUtc,
                        Changed: false,
                        IsEvent: false));
                    break;
                default:
                    var current = result.Current;
                    _realtime.StateChanged(new StateDelta(attribute.AttributeId, current.Value, current.ObservedAtUtc));
                    _writeBehind.EnqueueState(new PendingAttributeState(
                        attribute.AttributeId,
                        current.Value,
                        current.ObservedAtUtc,
                        current.LastChangedAtUtc));
                    var changed = result.Outcome == StateApplyOutcome.Changed;
                    if (changed && (isEvent || attribute.Definition?.Recorded == true))
                    {
                        _writeBehind.EnqueueHistory(new PendingHistoryRow(attribute.AttributeId, change.ObservedAtUtc, change.Value));
                    }

                    changes.Add(new AttributeStateChange(
                        attribute.AttributeId,
                        result.Previous?.Value,
                        current.Value,
                        current.ObservedAtUtc,
                        changed,
                        isEvent));
                    break;
            }

            MarkSeen(attribute.DeviceId);
        }

        if (changes.Count == 0)
        {
            return;
        }

        foreach (var subscriber in _activeSubscribers)
        {
            try
            {
                await subscriber.OnStateChangedAsync(changes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogSubscriberFailed(subscriber.GetType().Name, ex);
            }
        }
    }

    private async Task<AttributeInfo?> ResolveAsync(PendingChange change, CancellationToken cancellationToken)
    {
        if (change.Kind == ChangeKind.Injected)
        {
            return _registry.TryGet(change.AttributeId, out var injected) ? injected : null;
        }

        if (_registry.TryResolve(change.Address, out var known))
        {
            return known;
        }

        var now = _clock.Time.GetUtcNow();
        int failures;
        lock (_lock)
        {
            _unknown.TryGetValue(change.Address, out var entry);
            if (entry.Until > now)
            {
                return null;
            }

            failures = entry.Failures;
        }

        // Bounded: this loop serves every integration, so a slow or hanging lookup must not hold up the others.
        using var timeout = new CancellationTokenSource(ResolveTimeout, _clock.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var created = await _inventory.EnsureAttributeAsync(change.Address, linked.Token)
                .WaitAsync(linked.Token)
                .ConfigureAwait(false);
            if (created is null)
            {
                // Do not ask again for a minute about an address nobody knows.
                RememberUnknown(change.Address, new UnknownAddress(now + UnknownRetryAfter, 0));
                LogUnknownAttribute(change.Address.Device, change.Address.Endpoint, change.Address.Capability, change.Address.Attribute);
            }
            else if (failures > 0)
            {
                lock (_lock)
                {
                    _unknown.Remove(change.Address);
                }
            }

            return created;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Failed or timed out (database down, inventory busy): back off before asking again, doubling per failure.
            var backoff = TimeSpan.FromTicks(Math.Min(ResolveFailureBackoff.Ticks << Math.Min(failures, 8), UnknownRetryAfter.Ticks));
            RememberUnknown(change.Address, new UnknownAddress(now + backoff, failures + 1));
            LogResolveFailed(
                change.Address.Device,
                change.Address.Capability,
                change.Address.Attribute,
                timeout.IsCancellationRequested ? "Timeout" : ex.GetType().Name);
            return null;
        }
    }

    private void RememberUnknown(AttributeAddress address, UnknownAddress entry)
    {
        lock (_lock)
        {
            _unknown[address] = entry;
            if (_unknown.Count > 10_000)
            {
                _unknown.Clear();
            }
        }
    }

    private void ApplyAvailability(PendingChange change)
    {
        if (_registry.FindDevice(change.Address.IntegrationId, change.Address.Device) is not { } device
            || device.Availability == change.Availability)
        {
            return;
        }

        device.Availability = change.Availability;
        if (change.Availability == Availability.Online)
        {
            device.LastSeenAtUtc = change.ObservedAtUtc;
        }

        _writeBehind.EnqueueDevice(new PendingDeviceUpdate(
            device.DeviceId,
            change.Availability,
            change.Availability == Availability.Online ? change.ObservedAtUtc : null));
        _realtime.AvailabilityChanged(device.DeviceId, change.Availability);
    }

    private void MarkSeen(Guid deviceId)
    {
        if (_registry.FindDevice(deviceId) is not { } device)
        {
            return;
        }

        var now = _clock.Time.GetUtcNow();
        device.LastSeenAtUtc = now;
        if (device.LastSeenQueuedAtUtc is { } last && now - last < _options.LastSeenPersistInterval)
        {
            return;
        }

        device.LastSeenQueuedAtUtc = now;
        _writeBehind.EnqueueDevice(new PendingDeviceUpdate(deviceId, null, now));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The state pipeline failed to process a batch")]
    private partial void LogBatchFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "State subscriber {Subscriber} failed")]
    private partial void LogSubscriberFailed(string subscriber, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dropped a value of attribute {AttributeId} ({Capability}.{Attribute}) that its schema rejects")]
    private partial void LogInvalidValue(int attributeId, string capability, string attribute);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dropped a report for unknown attribute {Device}/{Endpoint}/{Capability}.{Attribute}")]
    private partial void LogUnknownAttribute(string device, string endpoint, string capability, string attribute);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not resolve attribute {Device}/{Capability}.{Attribute} ({ExceptionType})")]
    private partial void LogResolveFailed(string device, string capability, string attribute, string exceptionType);

    private readonly record struct ChangeKey(ChangeKind Kind, AttributeAddress Address, long Sequence);

    /// <summary>An address the inventory did not resolve: dropped until <see cref="Until"/>; <see cref="Failures"/> failed lookups in a row.</summary>
    private readonly record struct UnknownAddress(DateTimeOffset Until, int Failures);

    private sealed record PendingChange(
        ChangeKind Kind,
        AttributeAddress Address,
        int AttributeId,
        StateValue Value,
        DateTimeOffset ObservedAtUtc,
        Availability Availability);
}

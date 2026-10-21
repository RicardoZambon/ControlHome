using System.Collections.Concurrent;
using System.Globalization;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.SmartThings.Api;
using ControlHome.Connectors.SmartThings.Auth;
using ControlHome.Connectors.SmartThings.Mapping;
using ControlHome.Connectors.SmartThings.Model;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ControlHome.Connectors.SmartThings.Runtime;

/// <summary>
/// One SmartThings location. Polls <c>GET /v1/devices?locationId&amp;includeStatus&amp;includeHealth&amp;includeRestricted</c>
/// every 30–60 s (stretched while the rate-limit headers say the budget runs low, paused until the window resets when it
/// is used up, blocked after a 429 until the vendor's reset), and after a command polls that device's status every 5 s
/// for 30 s (<see cref="AdaptivePoller"/>; status reads and commands together stay within 12 per minute per device).
/// Rooms are read at start and every 6 hours (persisted hourly quota); capability definitions for unmapped capabilities
/// are fetched a few per poll and cached. A full listing is the full inventory (devices missing from it are orphaned,
/// never deleted); a partial one only adds and updates. Only changed values are reported, with the vendor's timestamps.
/// </summary>
internal sealed partial class SmartThingsRuntime : IConnectorRuntime
{
    private readonly IntegrationInstance _instance;
    private readonly SmartThingsSettings _settings;
    private readonly Func<HttpClient> _httpFactory;
    private readonly IQuotaCounter? _quotas;
    private readonly SmartThingsOptions _options;
    private readonly CapabilityDefinitionCache _definitions;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, MappedDevice> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeviceStatus> _statuses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _inventory = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Device, string Endpoint, string Capability, string Attribute), StateValue> _reported = [];
    private readonly Dictionary<string, Availability> _availability = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Device, string Endpoint), DateTimeOffset> _buttons = [];
    private readonly Dictionary<(string Device, string Endpoint), DateTimeOffset> _energyWindows = [];
    private readonly Dictionary<string, HotDevice> _hot = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClockedSlidingWindow> _deviceLimiters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _usedKeys = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string> _rooms = new Dictionary<string, string>();
    private DateTimeOffset? _roomsFetchedAt;
    private DateTimeOffset _nextBulkAt = DateTimeOffset.MinValue;
    private int _lastPages = 1;
    private bool _inventorySynced;

    /// <summary>Devices the mapper failed on in their last mapping (used by the poll loop and hot polls; thread-safe).</summary>
    private readonly ConcurrentDictionary<string, bool> _mapFailures = new(StringComparer.Ordinal);
    private IntegrationStatus? _blocked;
    private string? _blockedMessage;
    private IntegrationStatus? _status;
    private string? _statusMessage;
    private IConnectorContext? _context;
    private SmartThingsApi? _api;
    private IAccessTokenSource? _tokens;
    private AdaptivePoller? _poller;
    private ClockedSlidingWindow? _listLimiter;
    private IQuotaCounter? _quota;
    private long _polls;
    private long _iterations;

    public SmartThingsRuntime(
        IntegrationInstance instance,
        SmartThingsSettings settings,
        Func<HttpClient> http,
        IQuotaCounter? quotas,
        SmartThingsOptions options,
        CapabilityDefinitionCache definitions)
    {
        _instance = instance;
        _settings = settings;
        _httpFactory = http;
        _quotas = quotas;
        _options = options;
        _definitions = definitions;
    }

    public SmartThingsSettings Settings => _settings;

    /// <summary>Bulk polls done (tests wait on it).</summary>
    internal long Polls => Interlocked.Read(ref _polls);

    /// <summary>Loop iterations finished (each ends by waiting for the next one; tests synchronize the fake clock on it).</summary>
    internal long Iterations => Interlocked.Read(ref _iterations);

    /// <summary>The token source of the current run (tests).</summary>
    internal IAccessTokenSource? Tokens => _tokens;

    /// <summary>The API client of the current run (tests: rate-limit buckets, call counter).</summary>
    internal SmartThingsApi? Api => _api;

    private ILogger Logger => _context?.Logger ?? NullLogger.Instance;

    public async Task RunAsync(IConnectorContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_lock)
        {
            if (_context is not null)
            {
                throw new InvalidOperationException("This SmartThings integration is already running.");
            }

            _context = context;
        }

        using var http = _httpFactory();
        try
        {
            SetStatus(context, IntegrationStatus.Connecting, null);
            _quota = _quotas ?? new InMemoryQuotaCounter(context.Time);
            _listLimiter = new ClockedSlidingWindow(_options.ListRequestsPerMinute, TimeSpan.FromMinutes(1), context.Time);
            _poller = new AdaptivePoller(context.Time, _settings.PollInterval, _options.HotPollInterval, _options.HotPollWindow, _options.Jitter, _options.Random);
            var tokens = await CreateTokensAsync(context, http, cancellationToken).ConfigureAwait(false);
            if (tokens is null)
            {
                await ParkAsync(context, IntegrationStatus.NeedsReauth, NeedsReauthMessage(), cancellationToken).ConfigureAwait(false);
                return;
            }

            _tokens = tokens;
            _api = new SmartThingsApi(http, _options, tokens, context.Time, new RateLimitTracker());
            await LoopAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped.
        }
        finally
        {
            lock (_lock)
            {
                _context = null;
                _api = null;
                _hot.Clear();
            }

            _listLimiter?.Dispose();
        }
    }

    public async Task<CommandOutcome> ExecuteAsync(DeviceCommandBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        IConnectorContext context;
        SmartThingsApi api;
        MappedDevice? device;
        lock (_lock)
        {
            if (_context is null || _api is null)
            {
                return CommandOutcome.Reject(CommandRejectionCodes.Unavailable, "The SmartThings integration is not running.");
            }

            context = _context;
            api = _api;
            device = _devices.GetValueOrDefault(batch.ExternalDeviceId);
            if (_blocked is { } blocked)
            {
                return CommandOutcome.Reject(CommandRejectionCodes.Unavailable, $"SmartThings is not available ({blocked}).");
            }
        }

        if (device is null)
        {
            return CommandOutcome.Reject(CommandRejectionCodes.UnknownDevice, "No such SmartThings device.");
        }

        var planned = CommandPlanner.Plan(device, batch);
        if (planned.Rejection is { } rejection)
        {
            return rejection;
        }

        var safetyCritical = batch.Commands.Any(c =>
            CapabilityCatalog.Find(c.CapabilityId)?.FindCommand(c.Command)?.IsSafetyCriticalFor(device.Device.DeviceClass) == true);
        var requests = CommandPlanner.Requests(planned.Commands);
        var limiter = DeviceLimiter(batch.ExternalDeviceId, context.Time);
        lock (_lock)
        {
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
            else if (_usedKeys.ContainsKey(batch.IdempotencyKey))
            {
                return CommandOutcome.Reject(CommandRejectionCodes.Duplicate, "This command was already executed.");
            }

            if (limiter.Available < requests.Count)
            {
                return CommandOutcome.Reject(
                    CommandRejectionCodes.Unavailable,
                    "SmartThings allows 12 requests per device and minute; try again in a few seconds.");
            }

            // At-most-once: from here on the key stays used, whatever happens to the request.
            if (!string.IsNullOrEmpty(batch.IdempotencyKey))
            {
                _usedKeys[batch.IdempotencyKey] = now;
            }
        }

        for (var i = 0; i < requests.Count; i++)
        {
            limiter.TryAcquire();
            IReadOnlyList<string> statuses;
            try
            {
                statuses = await api.SendCommandsAsync(batch.ExternalDeviceId, requests[i], cancellationToken).ConfigureAwait(false);
            }
            catch (SmartThingsApiException exception)
            {
                LogCommandFailed(context.Logger, batch.ExternalDeviceId, exception.Kind, exception.Status is { } status ? (int)status : 0);
                Block(context, exception);
                var sent = i == 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" Only the first {i * CommandPlanner.MaxCommandsPerRequest} commands reached SmartThings.");
                return exception.Kind switch
                {
                    ConnectorErrorKind.Permanent when exception.Status is System.Net.HttpStatusCode.BadRequest =>
                        CommandOutcome.Reject(CommandRejectionCodes.InvalidArguments, "SmartThings rejected the command's arguments." + sent),
                    ConnectorErrorKind.Permanent => CommandOutcome.Reject(CommandRejectionCodes.Refused, exception.Message + sent),
                    _ => CommandOutcome.Reject(CommandRejectionCodes.Unavailable, exception.Message + sent),
                };
            }

            if (statuses.Any(s => string.Equals(s, "FAILED", StringComparison.OrdinalIgnoreCase)))
            {
                NotifyHot(batch.ExternalDeviceId, context.Time.GetUtcNow());
                return CommandOutcome.Reject(CommandRejectionCodes.Refused, "SmartThings reported that the device did not run the command.");
            }
        }

        // ACCEPTED (or COMPLETED) is only a receipt: the state confirms it, so the device is polled closely for a while.
        NotifyHot(batch.ExternalDeviceId, context.Time.GetUtcNow());
        return CommandOutcome.AcceptedOutcome;
    }

    public async Task RefreshAsync(string externalDeviceId, CancellationToken cancellationToken)
    {
        IConnectorContext? context;
        SmartThingsApi? api;
        lock (_lock)
        {
            context = _context;
            api = _api;
            if (context is null || api is null || _blocked is not null || !_devices.ContainsKey(externalDeviceId))
            {
                return;
            }
        }

        if (DeviceLimiter(externalDeviceId, context.Time).TryAcquire(_options.CommandReserve))
        {
            try
            {
                await PollDeviceAsync(context, api, externalDeviceId, cancellationToken).ConfigureAwait(false);
            }
            catch (SmartThingsApiException exception)
            {
                LogDevicePollFailed(context.Logger, externalDeviceId, exception.Kind);
                Block(context, exception);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            foreach (var limiter in _deviceLimiters.Values)
            {
                limiter.Dispose();
            }

            _deviceLimiters.Clear();
        }

        return ValueTask.CompletedTask;
    }

    // ---- the loop -----------------------------------------------------------------------------------------------------

    private async Task LoopAsync(IConnectorContext context, CancellationToken cancellationToken)
    {
        var failures = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan? wait = null;
            try
            {
                if (context.Time.GetUtcNow() >= _nextBulkAt)
                {
                    await PollAllAsync(context, cancellationToken).ConfigureAwait(false);
                    failures = 0;
                }

                await HotPollAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (SmartThingsApiException exception) when (!cancellationToken.IsCancellationRequested)
            {
                wait = Failed(context, exception, failures++);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // A bug (vendor payload drift the mapper did not survive, …): report it, keep the integration alive.
                LogPollCrashed(context.Logger, exception);
                SetStatus(context, IntegrationStatus.Error, "Reading SmartThings failed unexpectedly; retrying. The server log has the details.");
                wait = _options.ErrorRetry;
            }

            Interlocked.Increment(ref _iterations);
            if (wait is { } delay)
            {
                await Task.Delay(delay, context.Time, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _poller!.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The status for a failed poll and how long to wait before the next one.</summary>
    private TimeSpan Failed(IConnectorContext context, SmartThingsApiException exception, int failures)
    {
        LogPollFailed(context.Logger, exception.Kind, exception.Status is { } status ? (int)status : 0);
        switch (exception.Kind)
        {
            case ConnectorErrorKind.Unauthorized:
                Block(context, exception);
                return _options.NeedsReauthRetry;
            case ConnectorErrorKind.PlanRequired:
                Block(context, exception);
                return _options.PlanRequiredRetry;
            case ConnectorErrorKind.RateLimited:
                SetStatus(context, IntegrationStatus.Degraded, exception.Message);
                return exception.Error.RetryAfter is { } after && after > TimeSpan.FromSeconds(1) ? after : TimeSpan.FromSeconds(1);
            case ConnectorErrorKind.Transient:
                if (failures >= 2)
                {
                    MarkAllUnknown(context);
                }

                SetStatus(context, IntegrationStatus.Degraded, "SmartThings cannot be reached; retrying.");
                return Backoff(failures);
            default:
                SetStatus(context, IntegrationStatus.Error, exception.Message);
                return _options.ErrorRetry;
        }
    }

    /// <summary>Stops polling and commands while SmartThings needs the user (re-authorize, a paid plan).</summary>
    private void Block(IConnectorContext context, SmartThingsApiException exception)
    {
        var status = exception.Kind switch
        {
            ConnectorErrorKind.Unauthorized => IntegrationStatus.NeedsReauth,
            ConnectorErrorKind.PlanRequired => IntegrationStatus.PlanRequired,
            _ => (IntegrationStatus?)null,
        };
        if (status is not { } blocked)
        {
            return;
        }

        var message = blocked == IntegrationStatus.NeedsReauth ? NeedsReauthMessage() : PlanRequiredMessage;
        lock (_lock)
        {
            _blocked = blocked;
            _blockedMessage = message;
        }

        MarkAllUnknown(context);
        SetStatus(context, blocked, message);
    }

    private TimeSpan Backoff(int failures)
    {
        var delay = _options.TransientBackoffInitial * Math.Pow(2, Math.Min(failures, 10));
        if (delay > _options.TransientBackoffMax)
        {
            delay = _options.TransientBackoffMax;
        }

        var random = _options.Random ?? Random.Shared;
        double factor;
        lock (_lock)
        {
            factor = 1 + (_options.Jitter * ((2 * random.NextDouble()) - 1));
        }

        return delay * factor;
    }

    private async Task ParkAsync(IConnectorContext context, IntegrationStatus status, string message, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _blocked = status;
            _blockedMessage = message;
        }

        SetStatus(context, status, message);
        while (true)
        {
            await Task.Delay(_options.NeedsReauthRetry, context.Time, cancellationToken).ConfigureAwait(false);
        }
    }

    // ---- bulk poll ----------------------------------------------------------------------------------------------------

    private async Task PollAllAsync(IConnectorContext context, CancellationToken cancellationToken)
    {
        var api = _api!;
        var now = context.Time.GetUtcNow();

        // Header-driven budget: wait for the window to reset rather than spend the last calls (commands need some).
        var budget = api.Limits[RateLimitTracker.Devices];
        var availableAt = budget.AvailableAt(now, _lastPages, reserve: 1);
        if (availableAt > now)
        {
            _nextBulkAt = availableAt;
            LogBudgetWait(context.Logger, (availableAt - now).TotalSeconds);
            return;
        }

        if (_listLimiter!.Available < _lastPages)
        {
            _nextBulkAt = now + TimeSpan.FromSeconds(10);
            return;
        }

        await RefreshRoomsIfDueAsync(context, api, now, force: false, cancellationToken).ConfigureAwait(false);
        var listing = await api.ListDevicesAsync(_settings.LocationId, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < listing.Pages; i++)
        {
            _listLimiter.TryAcquire();
        }

        _lastPages = Math.Max(1, listing.Pages);
        var parsed = new List<(SmartThingsDevice Device, DeviceStatus Status)>();
        foreach (var item in listing.Items)
        {
            if (SmartThingsDevice.Parse(item) is { } device && (device.LocationId is null || device.LocationId == _settings.LocationId))
            {
                parsed.Add((device, DeviceStatus.FromListing(item)));
            }
        }

        // A room nobody knows yet (added in the app): read the rooms again (rate-limited by the quota).
        if (parsed.Any(p => p.Device.RoomId is { } room && !_rooms.ContainsKey(room)))
        {
            await RefreshRoomsIfDueAsync(context, api, now, force: true, cancellationToken).ConfigureAwait(false);
        }

        var ids = parsed.Select(p => p.Device.DeviceId).ToHashSet(StringComparer.Ordinal);
        var mapped = MapAll(context, parsed, ids, now);
        if (await FetchDefinitionsAsync(context, api, mapped, now, cancellationToken).ConfigureAwait(false))
        {
            mapped = MapAll(context, parsed, ids, now);
        }

        // A device the mapper failed on is not "gone": the listing then only adds and updates (nothing is orphaned).
        var failed = parsed.Any(p => _mapFailures.ContainsKey(p.Device.DeviceId));
        await SyncInventoryAsync(context, mapped, listing.Complete && !failed, cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            foreach (var (device, status) in parsed)
            {
                _statuses[device.DeviceId] = status;
            }

            _blocked = null;
            _blockedMessage = null;
        }

        foreach (var device in mapped)
        {
            Report(context, device);
        }

        Interlocked.Increment(ref _polls);
        var interval = budget.Headroom() < 0.5 ? _options.MaxPollInterval : _settings.PollInterval;
        if (interval < _settings.PollInterval)
        {
            interval = _settings.PollInterval;
        }

        _nextBulkAt = now + interval;
        SetStatus(context, IntegrationStatus.Ok, OkMessage());
    }

    private List<MappedDevice> MapAll(IConnectorContext context, IReadOnlyList<(SmartThingsDevice Device, DeviceStatus Status)> devices, IReadOnlySet<string> ids, DateTimeOffset now)
    {
        var mapContext = new MapContext(now, _rooms, _definitions.Lookup, ids);
        var mapped = new List<MappedDevice>();
        foreach (var (device, status) in devices)
        {
            try
            {
                if (SmartThingsDeviceMapper.Map(device, status, mapContext) is { } result)
                {
                    mapped.Add(result);
                }

                _mapFailures.TryRemove(device.DeviceId, out _);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Vendor payload drift must never stop the integration: skip the device this time.
                _mapFailures[device.DeviceId] = true;
                LogMapFailed(context.Logger, exception, device.DeviceId);
            }
        }

        return mapped;
    }

    /// <summary>Fetches a few missing capability definitions; <c>true</c> when any arrived (the devices are mapped again).</summary>
    private async Task<bool> FetchDefinitionsAsync(IConnectorContext context, SmartThingsApi api, IReadOnlyList<MappedDevice> devices, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Missing definitions first, then cached ones that expired (only capabilities that went the generic way are cached).
        var due = devices
            .SelectMany(d => d.MissingDefinitions)
            .Concat(devices.SelectMany(d => d.Source.Components.SelectMany(c => c.Capabilities)).Where(c => _definitions.Lookup(c).State != DefinitionState.Pending))
            .DistinctBy(c => (c.Id, c.Version))
            .Where(c => _definitions.IsDue(c, now))
            .Take(_options.DefinitionFetchesPerPoll)
            .ToList();
        var fetched = false;
        foreach (var capability in due)
        {
            try
            {
                var document = await api.GetCapabilityAsync(capability.Id, capability.Version, cancellationToken).ConfigureAwait(false);
                _definitions.Store(capability, document, now);
                fetched = true;
                LogDefinition(context.Logger, capability.Id, capability.Version, document is not null);
            }
            catch (SmartThingsApiException exception) when (exception.Kind is ConnectorErrorKind.RateLimited or ConnectorErrorKind.Transient or ConnectorErrorKind.Permanent)
            {
                // Try again in a later poll; the devices work without these capabilities meanwhile.
                LogDefinitionFailed(context.Logger, capability.Id, exception.Kind);
                break;
            }
        }

        return fetched;
    }

    private async Task RefreshRoomsIfDueAsync(IConnectorContext context, SmartThingsApi api, DateTimeOffset now, bool force, CancellationToken cancellationToken)
    {
        var age = _roomsFetchedAt is { } fetched ? now - fetched : TimeSpan.MaxValue;
        if (age < (force ? TimeSpan.FromMinutes(15) : _options.RoomsRefresh))
        {
            return;
        }

        var limit = new QuotaLimit($"{SmartThingsConnectorFactory.Key}:{_instance.Id:D}:rooms", QuotaPeriod.Hour, _options.RoomsPerHour);
        if (!await _quota!.TryConsumeAsync(limit, 1, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        _roomsFetchedAt = now;
        try
        {
            var rooms = await api.ListRoomsAsync(_settings.LocationId, cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                _rooms = rooms;
            }
        }
        catch (SmartThingsApiException exception) when (exception.Kind is ConnectorErrorKind.RateLimited or ConnectorErrorKind.Transient or ConnectorErrorKind.Permanent)
        {
            // Rooms are only suggestions for importing devices.
            LogRoomsFailed(context.Logger, exception.Kind);
        }
    }

    /// <summary>Saves the inventory when it changed: a complete listing is the full inventory, a partial one only adds and updates.</summary>
    private async Task SyncInventoryAsync(IConnectorContext context, IReadOnlyList<MappedDevice> listed, bool complete, CancellationToken cancellationToken)
    {
        // A vendor glitch listing a device twice must not fail the poll.
        var devices = listed.DistinctBy(d => d.Device.ExternalId, StringComparer.Ordinal).ToList();
        var serialized = devices.ToDictionary(d => d.Device.ExternalId, d => CapabilityJson.Serialize(d.Device), StringComparer.Ordinal);
        bool changed;
        lock (_lock)
        {
            // The first complete listing is always saved: it also orphans devices that left while ControlHome was down.
            changed = (complete && !_inventorySynced)
                || serialized.Count != _inventory.Count
                || serialized.Any(s => !_inventory.TryGetValue(s.Key, out var last) || !string.Equals(last, s.Value, StringComparison.Ordinal));
        }

        if (changed)
        {
            if (complete)
            {
                try
                {
                    await context.SyncInventoryAsync([.. devices.Select(d => d.Device)], cancellationToken).ConfigureAwait(false);
                    lock (_lock)
                    {
                        _inventorySynced = true;
                        _inventory.Clear();
                        foreach (var (id, text) in serialized)
                        {
                            _inventory[id] = text;
                        }
                    }
                }
                catch (ArgumentException exception)
                {
                    // One device the host refuses (a mapping bug) must not hold back the others: save them one by one.
                    LogSyncRejected(context.Logger, exception);
                    await UpsertEachAsync(context, devices, serialized, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await UpsertEachAsync(context, devices, serialized, cancellationToken).ConfigureAwait(false);
            }
        }

        lock (_lock)
        {
            if (complete)
            {
                foreach (var gone in _devices.Keys.Where(id => !serialized.ContainsKey(id)).ToList())
                {
                    _devices.Remove(gone);
                    _statuses.Remove(gone);
                    _availability.Remove(gone);
                    _hot.Remove(gone);
                }
            }

            foreach (var device in devices)
            {
                if (_inventory.ContainsKey(device.Device.ExternalId))
                {
                    _devices[device.Device.ExternalId] = device;
                }
            }
        }
    }

    private async Task UpsertEachAsync(IConnectorContext context, IReadOnlyList<MappedDevice> devices, IReadOnlyDictionary<string, string> serialized, CancellationToken cancellationToken)
    {
        foreach (var device in devices)
        {
            var id = device.Device.ExternalId;
            bool changed;
            lock (_lock)
            {
                changed = !_inventory.TryGetValue(id, out var last) || !string.Equals(last, serialized[id], StringComparison.Ordinal);
            }

            if (!changed)
            {
                continue;
            }

            try
            {
                await context.UpsertDeviceAsync(device.Device, cancellationToken).ConfigureAwait(false);
                lock (_lock)
                {
                    _inventory[id] = serialized[id];
                }
            }
            catch (ArgumentException exception)
            {
                LogUpsertRejected(context.Logger, exception, id);
            }
        }
    }

    // ---- hot polls ----------------------------------------------------------------------------------------------------

    private void NotifyHot(string deviceId, DateTimeOffset now)
    {
        lock (_lock)
        {
            _hot[deviceId] = new HotDevice(now + _options.HotPollWindow, now);
        }

        _poller?.NotifyCommand();
    }

    private async Task HotPollAsync(IConnectorContext context, CancellationToken cancellationToken)
    {
        var api = _api!;
        var now = context.Time.GetUtcNow();
        List<string> due;
        lock (_lock)
        {
            foreach (var expired in _hot.Where(h => h.Value.Until <= now).Select(h => h.Key).ToList())
            {
                _hot.Remove(expired);
            }

            due = [.. _hot.Where(h => now - h.Value.LastPoll >= _options.HotPollInterval - TimeSpan.FromMilliseconds(500)).Select(h => h.Key)];
        }

        foreach (var deviceId in due)
        {
            // Commands keep a reserve of the device's per-minute budget.
            if (!DeviceLimiter(deviceId, context.Time).TryAcquire(_options.CommandReserve))
            {
                continue;
            }

            lock (_lock)
            {
                if (_hot.TryGetValue(deviceId, out var hot))
                {
                    _hot[deviceId] = hot with { LastPoll = now };
                }
            }

            try
            {
                await PollDeviceAsync(context, api, deviceId, cancellationToken).ConfigureAwait(false);
            }
            catch (SmartThingsApiException exception) when (exception.Kind is not (ConnectorErrorKind.Unauthorized or ConnectorErrorKind.PlanRequired))
            {
                LogDevicePollFailed(context.Logger, deviceId, exception.Kind);
            }
        }
    }

    /// <summary>Reads one device's status (<c>GET /v1/devices/{id}/status</c>) and reports what changed.</summary>
    private async Task PollDeviceAsync(IConnectorContext context, SmartThingsApi api, string deviceId, CancellationToken cancellationToken)
    {
        var document = await api.GetDeviceStatusAsync(deviceId, cancellationToken).ConfigureAwait(false);
        var status = DeviceStatus.Parse(document);
        MappedDevice? previous;
        IReadOnlySet<string> ids;
        lock (_lock)
        {
            previous = _devices.GetValueOrDefault(deviceId);
            ids = _devices.Keys.ToHashSet(StringComparer.Ordinal);
            _statuses[deviceId] = status;
        }

        if (previous is null)
        {
            return;
        }

        var mapped = MapAll(context, [(previous.Source, status)], ids, context.Time.GetUtcNow());
        if (mapped.Count == 0)
        {
            return;
        }

        var device = mapped[0];
        var text = CapabilityJson.Serialize(device.Device);
        bool changed;
        lock (_lock)
        {
            changed = !_inventory.TryGetValue(deviceId, out var last) || !string.Equals(last, text, StringComparison.Ordinal);
        }

        if (changed)
        {
            // The status can reveal capabilities (a value appears); the next bulk poll would do the same.
            await UpsertEachAsync(context, mapped, new Dictionary<string, string>(StringComparer.Ordinal) { [deviceId] = text }, cancellationToken).ConfigureAwait(false);
        }

        Availability availability;
        lock (_lock)
        {
            // The status endpoint carries no health: keep the last known availability.
            availability = _availability.GetValueOrDefault(deviceId, device.Availability);
            device = device with { Availability = availability == Availability.Unknown ? Availability.Online : availability };
            _devices[deviceId] = device;
        }

        Report(context, device);
    }

    // ---- reporting ----------------------------------------------------------------------------------------------------

    private void Report(IConnectorContext context, MappedDevice device)
    {
        var id = device.Device.ExternalId;
        lock (_lock)
        {
            if (_blocked is null && (!_availability.TryGetValue(id, out var availability) || availability != device.Availability))
            {
                _availability[id] = device.Availability;
                context.ReportAvailability(id, device.Availability, device.Availability == Availability.Offline ? "SmartThings reports the device offline." : null);
            }

            foreach (var reading in device.Readings)
            {
                var key = (id, reading.Endpoint, reading.Capability, reading.Attribute);
                if (_reported.TryGetValue(key, out var last) && last.Equals(reading.Value))
                {
                    continue;
                }

                _reported[key] = reading.Value;
                context.ReportState(new StateReport(id, reading.Endpoint, reading.Capability, reading.Attribute, reading.Value, reading.ObservedAtUtc));
            }

            foreach (var press in device.Buttons)
            {
                // The first observation is history; a newer timestamp is a new press.
                var key = (id, press.Endpoint);
                if (_buttons.TryGetValue(key, out var last) && press.Timestamp > last)
                {
                    context.ReportEvent(new EventReport(id, press.Endpoint, CapabilityIds.Button, "press", press.Event, null, press.Timestamp));
                }

                if (!_buttons.TryGetValue(key, out last) || press.Timestamp > last)
                {
                    _buttons[key] = press.Timestamp;
                }
            }

            foreach (var window in device.EnergyWindows)
            {
                var key = (id, window.Endpoint);
                if (PowerConsumptionReport.IsNew(window, _energyWindows.TryGetValue(key, out var end) ? end : null))
                {
                    _energyWindows[key] = window.EndUtc;
                    OnEnergyWindow(context, id, window);
                }
            }
        }
    }

    /// <summary>
    /// A new <c>powerConsumptionReport</c> window. The SDK's energy-window report lands in a parallel milestone; until then
    /// the window is only logged (the cumulative counter already feeds <c>energy.energy</c>).
    /// </summary>
    private static void OnEnergyWindow(IConnectorContext context, string deviceId, PowerConsumptionWindow window) =>
        LogEnergyWindow(context.Logger, deviceId, window.Endpoint, window.StartUtc, window.EndUtc, window.DeltaWh);

    private void MarkAllUnknown(IConnectorContext context)
    {
        List<string> ids;
        lock (_lock)
        {
            ids = [.. _devices.Keys.Where(id => _availability.GetValueOrDefault(id) != Availability.Unknown)];
            foreach (var id in ids)
            {
                _availability[id] = Availability.Unknown;
            }
        }

        foreach (var id in ids)
        {
            context.ReportAvailability(id, Availability.Unknown, "SmartThings cannot be read right now.");
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private async Task<IAccessTokenSource?> CreateTokensAsync(IConnectorContext context, HttpClient http, CancellationToken cancellationToken)
    {
        if (_settings.Mode == SmartThingsAuthMode.PersonalAccessToken)
        {
            var pat = await _instance.Secrets.GetAsync(SecretKeys.PersonalAccessToken, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrEmpty(pat) ? null : new PersonalAccessToken(pat);
        }

        var tokens = TokenSet.Deserialize(await _instance.Secrets.GetAsync(SecretKeys.Tokens, cancellationToken).ConfigureAwait(false));
        var secret = await _instance.Secrets.GetAsync(SecretKeys.ClientSecret, cancellationToken).ConfigureAwait(false);
        if (tokens is null || string.IsNullOrEmpty(secret) || _settings.ClientId is null || _settings.Endpoints is null)
        {
            return null;
        }

        return new TokenManager(
            new OAuthClient(http, context.Time, _options.RequestTimeout),
            new OAuthApp(_settings.ClientId, secret, _settings.Endpoints),
            tokens,
            _instance.Secrets,
            context.Time,
            context.Logger,
            _options.RefreshBeforeExpiry,
            _options.KeepAlive);
    }

    private ClockedSlidingWindow DeviceLimiter(string deviceId, TimeProvider time)
    {
        lock (_lock)
        {
            if (!_deviceLimiters.TryGetValue(deviceId, out var limiter))
            {
                limiter = new ClockedSlidingWindow(_options.DeviceRequestsPerMinute, TimeSpan.FromMinutes(1), time);
                _deviceLimiters[deviceId] = limiter;
            }

            return limiter;
        }
    }

    private string NeedsReauthMessage() => _settings.Mode == SmartThingsAuthMode.PersonalAccessToken
        ? "SmartThings rejected the personal access token (they expire 24 h after they were created). Sign in again."
        : "SmartThings rejected the sign-in (revoked, expired after 30 days unused, or the app's secret changed). Sign in again.";

    private const string PlanRequiredMessage =
        "SmartThings asks for a paid plan (the Personal Plan) for API access. Subscribe in your Samsung account; ControlHome tries again every 30 minutes.";

    private string? OkMessage()
    {
        if (_tokens is TokenManager { HasUnsavedTokens: true })
        {
            return "The rotated SmartThings tokens could not be saved yet; retrying.";
        }

        if (_settings.Mode != SmartThingsAuthMode.PersonalAccessToken)
        {
            return null;
        }

        return _settings.PatIssuedAtUtc is { } issued
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Development mode: the personal access token expires 24 h after it was created (by {issued.AddHours(24):yyyy-MM-dd HH:mm} UTC at the latest). Use your own SmartThings app for everyday use.")
            : "Development mode: personal access tokens expire 24 h after they were created. Use your own SmartThings app for everyday use.";
    }

    /// <summary>Sets the integration status when it changed (a poll every 30 s must not push the same status every time).</summary>
    private void SetStatus(IConnectorContext context, IntegrationStatus status, string? message)
    {
        lock (_lock)
        {
            if (_status == status && string.Equals(_statusMessage, message, StringComparison.Ordinal))
            {
                return;
            }

            _status = status;
            _statusMessage = message;
        }

        context.SetStatus(status, message);
    }

    private sealed record HotDevice(DateTimeOffset Until, DateTimeOffset LastPoll);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SmartThings poll failed ({Kind}, HTTP {Status})")]
    private static partial void LogPollFailed(ILogger logger, ConnectorErrorKind kind, int status);

    [LoggerMessage(Level = LogLevel.Error, Message = "SmartThings poll failed unexpectedly")]
    private static partial void LogPollCrashed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "SmartThings: rate-limit budget low; next device poll in {Seconds} s")]
    private static partial void LogBudgetWait(ILogger logger, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SmartThings device {DeviceId} could not be mapped (skipped this poll)")]
    private static partial void LogMapFailed(ILogger logger, Exception exception, string deviceId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "SmartThings capability {CapabilityId}/{Version} definition fetched (found: {Found})")]
    private static partial void LogDefinition(ILogger logger, string capabilityId, int version, bool found);

    [LoggerMessage(Level = LogLevel.Information, Message = "SmartThings capability {CapabilityId} definition not fetched this time ({Kind})")]
    private static partial void LogDefinitionFailed(ILogger logger, string capabilityId, ConnectorErrorKind kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "SmartThings rooms not read this time ({Kind})")]
    private static partial void LogRoomsFailed(ILogger logger, ConnectorErrorKind kind);

    [LoggerMessage(Level = LogLevel.Error, Message = "SmartThings inventory was rejected by the host; saving devices one by one")]
    private static partial void LogSyncRejected(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "SmartThings device {DeviceId} was rejected by the inventory (skipped)")]
    private static partial void LogUpsertRejected(ILogger logger, Exception exception, string deviceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "SmartThings status of device {DeviceId} not read ({Kind})")]
    private static partial void LogDevicePollFailed(ILogger logger, string deviceId, ConnectorErrorKind kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SmartThings command for device {DeviceId} failed ({Kind}, HTTP {Status})")]
    private static partial void LogCommandFailed(ILogger logger, string deviceId, ConnectorErrorKind kind, int status);

    [LoggerMessage(Level = LogLevel.Debug, Message = "SmartThings energy window {DeviceId}/{Endpoint} {StartUtc:O}–{EndUtc:O}: {DeltaWh} Wh")]
    private static partial void LogEnergyWindow(ILogger logger, string deviceId, string endpoint, DateTimeOffset startUtc, DateTimeOffset endUtc, double? deltaWh);
}

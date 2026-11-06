using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Channels;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.LgThinQ.Api;
using ControlHome.Connectors.LgThinQ.Energy;
using ControlHome.Connectors.LgThinQ.Model;
using ControlHome.Connectors.LgThinQ.Mqtt;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ControlHome.Connectors.LgThinQ;

/// <summary>
/// One LG ThinQ integration. A session: discover (<c>GET devices</c> + profiles → full inventory; a listed appliance whose
/// profile LG cannot answer for now keeps its cached profile, else the inventory is not claimed complete so nothing is
/// orphaned), poll each device's state once (at most every 5 minutes per device), subscribe every device's push and event
/// notifications (1207 = already subscribed) and the device-list changes, then run in parallel:
/// <list type="bullet">
/// <item>the MQTT loop — register the client (<c>POST client</c>, allowExist), stored client certificate (issued through a
/// CSR when missing, rejected or expiring, at most once a minute), <c>GET route</c>, TLS on 8883 with the bundled AWS root,
/// QoS 1 subscription; reconnecting with exponential back-off and jitter (AWS drops sessions routinely; the back-off only
/// starts over after a connection that lasted), re-posting event subscriptions and re-polling stale devices after each
/// reconnect. A refused or unusable setup answer degrades to polling and is retried; it never stops the session;</item>
/// <item>the message loop — <c>DEVICE_STATUS</c> partial reports merged into the device's status (newest wins, also over a
/// poll that was on its way) and reported, <c>DEVICE_PUSH</c> codes as notification events, device-list changes as a
/// rediscovery;</item>
/// <item>a daily subscription refresh, a poller (each device at most every 5 minutes: all while MQTT is down, and always the
/// appliances MQTT does not report), and the nightly energy run (random time 01:00–05:59 local: daily and monthly Wh →
/// energy windows).</item>
/// </list>
/// All calls share the token's budget. A rejected token sets <see cref="IntegrationStatus.NeedsReauth"/> and the runtime
/// waits (the replace-token flow restarts it); the call limit (1306) sets <see cref="IntegrationStatus.QuotaExceeded"/>
/// and pauses everything for an hour; account problems (terms, country) are retried hourly.
/// </summary>
internal sealed partial class LgThinQRuntime : IConnectorRuntime
{
    private const int InboxCapacity = 10_000;

    private readonly IntegrationInstance _instance;
    private readonly LgThinQSettings _settings;
    private readonly LgThinQConnectorFactory _factory;
    private readonly LgIntegrationMemory _memory;
    private readonly LgMqttTransportFactory _transports;
    private readonly LgRuntimeOptions _options;
    private readonly Random _random;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, LgDeviceModel> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _usedKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Availability> _availability = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pollOnly = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _rediscover = new(0, 1);
    private IConnectorContext? _context;
    private LgApiClient? _api;
    private volatile bool _mqttConnected;
    private long _connections;
    private long _energyRuns;

    internal LgThinQRuntime(
        IntegrationInstance instance,
        LgThinQSettings settings,
        LgThinQConnectorFactory factory,
        LgIntegrationMemory memory,
        LgMqttTransportFactory transports,
        Random? random = null)
    {
        _instance = instance;
        _settings = settings;
        _factory = factory;
        _memory = memory;
        _transports = transports;
        _options = factory.Options;
        _random = random ?? Random.Shared;
    }

    public LgThinQSettings Settings => _settings;

    /// <summary>Successful MQTT connections so far (tests wait on it).</summary>
    internal long Connections => Interlocked.Read(ref _connections);

    /// <summary>Finished nightly energy runs (tests wait on it).</summary>
    internal long EnergyRuns => Interlocked.Read(ref _energyRuns);

    internal bool MqttConnected => _mqttConnected;

    /// <summary>When the nightly energy run is due (set once its timer is armed; tests move the clock to it).</summary>
    internal DateTimeOffset? NextEnergyRun { get; private set; }

    internal IReadOnlyList<LgDeviceModel> Devices
    {
        get
        {
            lock (_lock)
            {
                return [.. _devices.Values];
            }
        }
    }

    private ILogger Logger => _context?.Logger ?? NullLogger.Instance;

    public async Task RunAsync(IConnectorContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_lock)
        {
            if (_context is not null)
            {
                throw new InvalidOperationException("This LG ThinQ integration is already running.");
            }

            _context = context;
        }

        try
        {
            context.SetStatus(IntegrationStatus.Connecting, null);
            var pat = await _instance.Secrets.GetAsync(LgSecrets.Pat, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(pat))
            {
                context.SetStatus(IntegrationStatus.NeedsReauth, "No LG ThinQ access token is stored. Run the integration's flow to add one.");
                await Task.Delay(Timeout.InfiniteTimeSpan, context.Time, cancellationToken).ConfigureAwait(false);
                return;
            }

            var clientId = await _instance.Secrets.GetAsync(LgSecrets.ClientId, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(clientId))
            {
                clientId = LgThinQSetupFlow.NewClientId();
                await _instance.Secrets.SetAsync(LgSecrets.ClientId, clientId, cancellationToken).ConfigureAwait(false);
            }

            _api = _factory.CreateApi(_settings, pat, clientId, context.Time, context.Logger);
            while (true)
            {
                try
                {
                    await RunSessionAsync(context, _api, clientId, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (LgHaltException halt)
                {
                    LogHalted(context.Logger, halt.Status, halt.Message);
                    var retry = Task.Delay(halt.RetryAfter ?? Timeout.InfiniteTimeSpan, context.Time, cancellationToken);
                    context.SetStatus(halt.Status, halt.Message);
                    await retry.ConfigureAwait(false);
                    context.SetStatus(IntegrationStatus.Connecting, null);
                }
            }
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
                _mqttConnected = false;
            }
        }
    }

    public async Task<CommandOutcome> ExecuteAsync(DeviceCommandBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        IConnectorContext context;
        LgApiClient api;
        LgDeviceModel? device;
        lock (_lock)
        {
            if (_context is null || _api is null)
            {
                return CommandOutcome.Reject(CommandRejectionCodes.Unavailable, "The LG ThinQ integration is not running.");
            }

            context = _context;
            api = _api;
            device = _devices.GetValueOrDefault(batch.ExternalDeviceId);
        }

        if (device is null)
        {
            return CommandOutcome.Reject(CommandRejectionCodes.UnknownDevice, "No such LG ThinQ appliance.");
        }

        var (calls, rejection) = device.Plan(batch, _memory.StatusOf(device.DeviceId).Current);
        if (rejection is not null)
        {
            return rejection;
        }

        lock (_lock)
        {
            var now = context.Time.GetUtcNow();
            foreach (var expired in _usedKeys.Where(k => now - k.Value > _options.IdempotencyWindow).Select(k => k.Key).ToList())
            {
                _usedKeys.Remove(expired);
            }

            if (!string.IsNullOrEmpty(batch.IdempotencyKey) && !_usedKeys.TryAdd(batch.IdempotencyKey, now))
            {
                return CommandOutcome.Reject(CommandRejectionCodes.Duplicate, "This command was already executed.");
            }
        }

        // At-most-once: a control call is never retried (the key stays used even when it fails).
        foreach (var call in calls!)
        {
            try
            {
                await api.ControlAsync(device.DeviceId, call.Payload, cancellationToken).ConfigureAwait(false);
            }
            catch (LgApiException exception)
            {
                LogCommandRejected(context.Logger, device.DeviceId, LgErrors.NameOf(exception.Code), exception.Code ?? "-");
                if (exception.Kind == LgErrorKind.QuotaExceeded)
                {
                    context.SetStatus(IntegrationStatus.QuotaExceeded, LgErrors.StatusMessage(exception));
                }

                return LgErrors.Rejection(exception);
            }
            catch (LgQuotaBlockedException exception)
            {
                return CommandOutcome.Reject(
                    CommandRejectionCodes.Unavailable,
                    $"The LG ThinQ call limit is used up; calls are paused until {exception.Until:HH:mm} UTC.");
            }
        }

        return CommandOutcome.AcceptedOutcome;
    }

    public async Task RefreshAsync(string externalDeviceId, CancellationToken cancellationToken)
    {
        IConnectorContext? context;
        LgApiClient? api;
        LgDeviceModel? device;
        lock (_lock)
        {
            (context, api, device) = (_context, _api, _devices.GetValueOrDefault(externalDeviceId));
        }

        if (context is null || api is null || device is null || !PollDue(device.DeviceId, context.Time.GetUtcNow()))
        {
            return; // at most every 5 minutes per device: MQTT reports changes as they happen
        }

        try
        {
            await PollAsync(context, api, device, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is LgApiException or LgQuotaBlockedException)
        {
            LogPollFailed(context.Logger, device.DeviceId, exception.Message);
        }
    }

    public ValueTask DisposeAsync()
    {
        _rediscover.Dispose();
        return ValueTask.CompletedTask;
    }

    // ---- session ------------------------------------------------------------------------------------------------------

    private async Task RunSessionAsync(IConnectorContext context, LgApiClient api, string clientId, CancellationToken cancellationToken)
    {
        await DiscoverAsync(context, api, cancellationToken).ConfigureAwait(false);
        await PollStaleAsync(context, api, cancellationToken).ConfigureAwait(false);
        await SubscribeAllAsync(context, api, push: true, cancellationToken).ConfigureAwait(false);
        context.SetStatus(IntegrationStatus.Degraded, "Connected to LG ThinQ; real-time updates are starting.");

        var inbox = Channel.CreateBounded<LgMqttMessage>(new BoundedChannelOptions(InboxCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task[] loops =
        [
            MqttLoopAsync(context, api, clientId, inbox.Writer, stop.Token),
            ProcessAsync(context, inbox.Reader, stop.Token),
            RefreshLoopAsync(context, api, stop.Token),
            PollLoopAsync(context, api, stop.Token),
            EnergyLoopAsync(context, api, stop.Token),
            RediscoveryLoopAsync(context, api, stop.Token),
        ];
        try
        {
            var first = await Task.WhenAny(loops).ConfigureAwait(false);
            await stop.CancelAsync().ConfigureAwait(false);
            await first.ConfigureAwait(false);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            inbox.Writer.TryComplete();
            foreach (var loop in loops)
            {
                await loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            _mqttConnected = false;
        }
    }

    /// <summary>
    /// Runs a background call until it succeeds: transient failures are retried with back-off (status Degraded), the call
    /// limit waits for the budget to open again (QuotaExceeded), a rejected token or an account problem ends the session
    /// (<see cref="LgHaltException"/>). Device-level errors are thrown to the caller.
    /// </summary>
    private async Task<T> CallAsync<T>(IConnectorContext context, Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        var failures = 0;
        while (true)
        {
            TimeSpan wait;
            IntegrationStatus status;
            string message;
            try
            {
                return await call(cancellationToken).ConfigureAwait(false);
            }
            catch (LgQuotaBlockedException exception)
            {
                (status, message) = (IntegrationStatus.QuotaExceeded, $"The LG ThinQ call limit is used up; calls are paused until {exception.Until:HH:mm} UTC.");
                wait = exception.Until - context.Time.GetUtcNow();
            }
            catch (LgApiException exception) when (exception.Kind == LgErrorKind.QuotaExceeded)
            {
                (status, message) = (IntegrationStatus.QuotaExceeded, LgErrors.StatusMessage(exception));
                wait = (_api?.Budget.BlockedUntil ?? context.Time.GetUtcNow() + _options.Rate.QuotaBackoff) - context.Time.GetUtcNow();
            }
            catch (LgApiException exception) when (exception.Kind == LgErrorKind.NeedsReauth)
            {
                throw new LgHaltException(IntegrationStatus.NeedsReauth, LgErrors.StatusMessage(exception), null);
            }
            catch (LgApiException exception) when (exception.Kind == LgErrorKind.Account)
            {
                throw new LgHaltException(IntegrationStatus.Error, LgErrors.StatusMessage(exception), _options.AccountRetry);
            }
            catch (LgApiException exception) when (exception.Kind == LgErrorKind.Transient)
            {
                (status, message) = (IntegrationStatus.Degraded, $"LG ThinQ is not reachable right now ({exception.Message}); retrying.");
                wait = Backoff(_options.TransientRetryInitial, _options.TransientRetryMax, failures++);
            }

            // The timer is armed before the status is published, so whoever reacts to the status (a test clock) finds it.
            var delay = Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(1), context.Time, cancellationToken);
            context.SetStatus(status, message);
            await delay.ConfigureAwait(false);
        }
    }

    private async Task CallAsync(IConnectorContext context, Func<CancellationToken, Task> call, CancellationToken cancellationToken) =>
        await CallAsync<bool>(
                context,
                async ct =>
                {
                    await call(ct).ConfigureAwait(false);
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);

    // ---- discovery ----------------------------------------------------------------------------------------------------

    private async Task DiscoverAsync(IConnectorContext context, LgApiClient api, CancellationToken cancellationToken)
    {
        var list = LgDeviceInfo.ParseList(await CallAsync(context, ct => api.GetDevicesAsync(ct), cancellationToken).ConfigureAwait(false));
        var parents = LgDeviceMapper.Parents(list);
        var models = new List<LgDeviceModel>();
        var unanswered = new List<string>();
        foreach (var info in list)
        {
            LgCachedProfile? cached;
            try
            {
                cached = await ProfileAsync(context, api, info, cancellationToken).ConfigureAwait(false);
            }
            catch (LgApiException exception) when (exception.Kind is LgErrorKind.Device)
            {
                // Gone meanwhile, not owned, or a model ThinQ Connect does not support: not part of the inventory.
                LogProfileFailed(context.Logger, info.DeviceId, info.DeviceType, LgErrors.NameOf(exception.Code));
                continue;
            }
            catch (LgApiException exception) when (exception.Kind is LgErrorKind.Other or LgErrorKind.CommandRejected)
            {
                // LG lists the appliance but cannot answer its profile right now: the last profile stands in, else the
                // appliance waits for the next discovery — it is listed, so it must not be orphaned.
                LogProfileFailed(context.Logger, info.DeviceId, info.DeviceType, LgErrors.NameOf(exception.Code));
                if (!_memory.Profiles.TryGetValue(info.DeviceId, out cached))
                {
                    unanswered.Add(info.DeviceId);
                    continue;
                }
            }

            var model = LgDeviceMapper.Map(info, cached.Profile, cached.Energy, parents.GetValueOrDefault(info.DeviceId));
            if (model.Discovered.Endpoints.Count > 0)
            {
                models.Add(model);
            }
        }

        if (unanswered.Count == 0)
        {
            try
            {
                await context.SyncInventoryAsync([.. models.Select(m => m.Discovered)], cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException exception)
            {
                // A mapping bug must not take every appliance down: report them one by one, skipping the rejected one.
                LogInventoryRejected(context.Logger, exception.Message);
                models = await UpsertEachAsync(context, models, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            // Not a full inventory: claiming one would orphan the appliances LG could not describe.
            LogInventoryIncomplete(context.Logger, unanswered.Count);
            models = await UpsertEachAsync(context, models, cancellationToken).ConfigureAwait(false);
        }

        lock (_lock)
        {
            // Appliances LG could not describe this time keep their last model (commands and reports keep working).
            var kept = unanswered.Select(id => _devices.GetValueOrDefault(id)).OfType<LgDeviceModel>().ToList();
            _devices.Clear();
            foreach (var model in models.Concat(kept))
            {
                _devices[model.DeviceId] = model;
            }
        }

        LogDiscovered(context.Logger, models.Count, list.Count);

        // What is already known (a restart within the process) is reported again at once.
        foreach (var model in models)
        {
            if (_memory.Statuses.TryGetValue(model.DeviceId, out var status) && status.IsKnown)
            {
                Report(context, model, status.Current);
            }
        }
    }

    /// <summary>Adds or updates the devices one by one (parents first), skipping any the inventory rejects; nothing is orphaned.</summary>
    private static async Task<List<LgDeviceModel>> UpsertEachAsync(IConnectorContext context, List<LgDeviceModel> models, CancellationToken cancellationToken)
    {
        var accepted = new List<LgDeviceModel>();
        foreach (var model in models.OrderBy(m => m.Discovered.ParentExternalId is null ? 0 : 1))
        {
            try
            {
                await context.UpsertDeviceAsync(model.Discovered, cancellationToken).ConfigureAwait(false);
                accepted.Add(model);
            }
            catch (ArgumentException rejected)
            {
                LogDeviceRejected(context.Logger, model.DeviceId, rejected.Message);
            }
        }

        return accepted;
    }

    private async Task<LgCachedProfile> ProfileAsync(IConnectorContext context, LgApiClient api, LgDeviceInfo info, CancellationToken cancellationToken)
    {
        var now = context.Time.GetUtcNow();
        var alias = info.Alias ?? string.Empty;
        if (_memory.Profiles.TryGetValue(info.DeviceId, out var cached) && now - cached.FetchedAtUtc < _options.ProfileMaxAge && cached.Alias == alias)
        {
            return cached;
        }

        var profile = await CallAsync(context, ct => api.GetProfileAsync(info.DeviceId, ct), cancellationToken).ConfigureAwait(false);
        JsonElement? energy = null;
        if (_settings.EnergyEnabled)
        {
            try
            {
                energy = await CallAsync(context, ct => api.GetEnergyProfileAsync(info.DeviceId, ct), cancellationToken).ConfigureAwait(false);
            }
            catch (LgApiException exception) when (exception.Kind is LgErrorKind.Device or LgErrorKind.Other or LgErrorKind.CommandRejected)
            {
                // No energy data for this appliance.
            }
        }

        cached = new LgCachedProfile(profile, energy, alias, now);
        _memory.Profiles[info.DeviceId] = cached;
        return cached;
    }

    private async Task RediscoveryLoopAsync(IConnectorContext context, LgApiClient api, CancellationToken cancellationToken)
    {
        while (true)
        {
            await _rediscover.WaitAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(_options.RediscoveryDebounce, context.Time, cancellationToken).ConfigureAwait(false);
            while (_rediscover.CurrentCount > 0)
            {
                await _rediscover.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            HashSet<string> before;
            lock (_lock)
            {
                before = [.. _devices.Keys];
            }

            await DiscoverAsync(context, api, cancellationToken).ConfigureAwait(false);
            List<LgDeviceModel> added;
            lock (_lock)
            {
                added = [.. _devices.Values.Where(d => !before.Contains(d.DeviceId))];
            }

            foreach (var device in added)
            {
                await SubscribeDeviceAsync(context, api, device, push: true, cancellationToken).ConfigureAwait(false);
                if (PollDue(device.DeviceId, context.Time.GetUtcNow()))
                {
                    await PollDeviceAsync(context, api, device, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private void RequestRediscovery()
    {
        try
        {
            if (_rediscover.CurrentCount == 0)
            {
                _rediscover.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // Already requested.
        }
    }

    // ---- state polling ------------------------------------------------------------------------------------------------

    private bool PollDue(string deviceId, DateTimeOffset now) =>
        !_memory.LastPoll.TryGetValue(deviceId, out var last) || now - last >= _options.PollInterval;

    /// <summary>Polls every device whose last poll is at least <see cref="LgRuntimeOptions.PollInterval"/> old.</summary>
    private async Task PollStaleAsync(IConnectorContext context, LgApiClient api, CancellationToken cancellationToken)
    {
        foreach (var device in Devices)
        {
            if (PollDue(device.DeviceId, context.Time.GetUtcNow()))
            {
                await PollDeviceAsync(context, api, device, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task PollDeviceAsync(IConnectorContext context, LgApiClient api, LgDeviceModel device, CancellationToken cancellationToken)
    {
        try
        {
            await CallAsync(context, ct => PollAsync(context, api, device, ct), cancellationToken).ConfigureAwait(false);
        }
        catch (LgApiException exception) when (exception.Kind is LgErrorKind.Device or LgErrorKind.Other or LgErrorKind.CommandRejected)
        {
            LogPollFailed(context.Logger, device.DeviceId, exception.Message);
            SetAvailability(context, device.DeviceId, exception.Code == "1222" ? Availability.Offline : Availability.Unknown);
        }
    }

    private async Task PollAsync(IConnectorContext context, LgApiClient api, LgDeviceModel device, CancellationToken cancellationToken)
    {
        _memory.LastPoll[device.DeviceId] = context.Time.GetUtcNow();
        var current = _memory.StatusOf(device.DeviceId);

        // The answer counts as of the moment it was asked for: reports that arrive meanwhile are newer and stay.
        var asked = current.Version;
        var state = await api.GetStateAsync(device.DeviceId, cancellationToken).ConfigureAwait(false);
        Report(context, device, current.Replace(state, asked));
    }

    /// <summary>
    /// Polls each device at most every <see cref="LgRuntimeOptions.PollInterval"/>: all of them while MQTT is down, and
    /// always those MQTT does not report (not reportable, or their event subscription was refused).
    /// </summary>
    private async Task PollLoopAsync(IConnectorContext context, LgApiClient api, CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(_options.PollCheckInterval, context.Time, cancellationToken).ConfigureAwait(false);
            if (!_mqttConnected)
            {
                await PollStaleAsync(context, api, cancellationToken).ConfigureAwait(false);
                continue;
            }

            foreach (var device in Devices.Where(d => !d.Reportable || IsPollOnly(d.DeviceId)))
            {
                if (PollDue(device.DeviceId, context.Time.GetUtcNow()))
                {
                    await PollDeviceAsync(context, api, device, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private bool IsPollOnly(string deviceId)
    {
        lock (_lock)
        {
            return _pollOnly.Contains(deviceId);
        }
    }

    // ---- subscriptions ------------------------------------------------------------------------------------------------

    private async Task SubscribeAllAsync(IConnectorContext context, LgApiClient api, bool push, CancellationToken cancellationToken)
    {
        if (push)
        {
            try
            {
                await CallAsync(context, ct => api.SubscribePushDevicesAsync(ct), cancellationToken).ConfigureAwait(false);
            }
            catch (LgApiException exception) when (exception.Kind is LgErrorKind.Device or LgErrorKind.Other or LgErrorKind.CommandRejected)
            {
                LogSubscribeFailed(context.Logger, "devices", LgErrors.NameOf(exception.Code));
            }
        }

        foreach (var device in Devices)
        {
            await SubscribeDeviceAsync(context, api, device, push, cancellationToken).ConfigureAwait(false);
        }

        _memory.LastEventSubscribe = context.Time.GetUtcNow();
    }

    private async Task SubscribeDeviceAsync(IConnectorContext context, LgApiClient api, LgDeviceModel device, bool push, CancellationToken cancellationToken)
    {
        if (push)
        {
            try
            {
                await CallAsync(context, ct => api.SubscribePushAsync(device.DeviceId, ct), cancellationToken).ConfigureAwait(false);
            }
            catch (LgApiException exception) when (exception.Kind is LgErrorKind.Device or LgErrorKind.Other or LgErrorKind.CommandRejected)
            {
                // Appliances without notifications (robot cleaners, microwaves): their state events still matter.
                LogSubscribeFailed(context.Logger, device.DeviceId, LgErrors.NameOf(exception.Code));
            }
        }

        try
        {
            await CallAsync(context, ct => api.SubscribeEventAsync(device.DeviceId, ct), cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                _pollOnly.Remove(device.DeviceId);
            }
        }
        catch (LgApiException exception) when (exception.Kind is LgErrorKind.Device or LgErrorKind.Other or LgErrorKind.CommandRejected)
        {
            // Some appliances (old models) cannot be subscribed: they are polled every 5 minutes instead.
            LogSubscribeFailed(context.Logger, device.DeviceId, LgErrors.NameOf(exception.Code));
            lock (_lock)
            {
                _pollOnly.Add(device.DeviceId);
            }
        }
    }

    /// <summary>Posts push and event subscriptions again every <see cref="LgRuntimeOptions.SubscriptionRefresh"/>.</summary>
    private async Task RefreshLoopAsync(IConnectorContext context, LgApiClient api, CancellationToken cancellationToken)
    {
        while (true)
        {
            var due = _memory.LastEventSubscribe + _options.SubscriptionRefresh - context.Time.GetUtcNow();
            await Task.Delay(due > TimeSpan.Zero ? due : TimeSpan.FromSeconds(1), context.Time, cancellationToken).ConfigureAwait(false);
            if (context.Time.GetUtcNow() - _memory.LastEventSubscribe >= _options.SubscriptionRefresh)
            {
                await SubscribeAllAsync(context, api, push: true, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // ---- MQTT ---------------------------------------------------------------------------------------------------------

    private async Task MqttLoopAsync(IConnectorContext context, LgApiClient api, string clientId, ChannelWriter<LgMqttMessage> inbox, CancellationToken cancellationToken)
    {
        var failures = 0;
        var rejections = 0;
        var registered = false;
        string? host = null;
        var port = 8883;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LgMqttCredentials credentials;
            try
            {
                if (!registered)
                {
                    // POST client (allowExist) once per session, like the SDK on every start.
                    await CallAsync(context, ct => api.RegisterClientAsync(ct), cancellationToken).ConfigureAwait(false);
                    registered = true;
                }

                credentials = await CredentialsAsync(context, api, reissue: rejections >= 2, cancellationToken).ConfigureAwait(false);
                if (host is null || failures >= 3)
                {
                    var route = await CallAsync(context, ct => api.GetRouteAsync(ct), cancellationToken).ConfigureAwait(false);
                    (host, port) = ParseRoute(route.Str("mqttServer"));
                }

                if (host is null)
                {
                    throw new LgMqttSetupException("LG ThinQ did not name an MQTT server (GET route)");
                }
            }
            catch (Exception exception) when (exception is LgMqttSetupException
                or LgApiException { Kind: LgErrorKind.Device or LgErrorKind.Other or LgErrorKind.CommandRejected })
            {
                // LG refused (or answered nothing usable to) the client, certificate or route request: real-time updates
                // are optional — keep polling and try again later; never stop the integration over it.
                var reason = exception is LgApiException refused ? $"{LgErrors.NameOf(refused.Code)} {refused.Code}" : exception.Message;
                LogMqttSetupFailed(context.Logger, reason);
                var wait = Task.Delay(Backoff(_options.ReconnectInitial, _options.ReconnectMax, failures++), context.Time, cancellationToken);
                context.SetStatus(IntegrationStatus.Degraded, $"LG ThinQ could not set up the real-time client ({reason}); retrying, polling meanwhile.");
                await wait.ConfigureAwait(false);
                continue;
            }

            if (rejections >= 2)
            {
                rejections = 0;
            }

            X509Certificate2 clientCertificate;
            try
            {
                clientCertificate = credentials.ClientCertificate();
            }
            catch (CryptographicException)
            {
                // Unusable stored key or certificate: issue a new one (at most once a minute).
                LogCredentialsUnusable(context.Logger);
                rejections = 2;
                continue;
            }

            using var certificate = clientCertificate;
            await using var transport = _transports(new LgMqttConnection(
                host,
                port,
                clientId,
                certificate,
                _factory.TrustedRoots,
                _options.MqttConnectTimeout,
                _options.MqttKeepAlive));
            var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.MessageReceived = message => inbox.WriteAsync(message, cancellationToken).AsTask();
            transport.Disconnected = reason =>
            {
                lost.TrySetResult(reason);
                return Task.CompletedTask;
            };

            string message;
            try
            {
                await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
                await transport.SubscribeAsync(credentials.Topic, cancellationToken).ConfigureAwait(false);
                var connectedAt = context.Time.GetUtcNow();
                rejections = 0;
                _mqttConnected = true;
                Interlocked.Increment(ref _connections);
                LogMqttConnected(context.Logger, host, port);
                context.SetStatus(IntegrationStatus.Ok, null);
                await OnReconnectedAsync(context, api, cancellationToken).ConfigureAwait(false);

                var reason = await WaitForLossAsync(context, transport, lost.Task, cancellationToken).ConfigureAwait(false);
                message = $"Lost the LG ThinQ real-time connection ({reason}); reconnecting, polling meanwhile.";
                LogMqttLost(context.Logger, reason);

                // Only a connection that lasted starts the back-off over: one the broker drops right away (a duplicate
                // client id, a refused session) keeps backing off instead of reconnecting every second.
                if (context.Time.GetUtcNow() - connectedAt >= _options.StableConnection)
                {
                    failures = 0;
                }
            }
            catch (LgMqttConnectException exception)
            {
                if (exception.Failure is LgMqttFailure.TlsRejected or LgMqttFailure.NotAuthorized)
                {
                    rejections++;
                }

                message = $"Cannot connect to LG ThinQ's real-time service ({exception.Message}); retrying, polling meanwhile.";
                LogMqttFailed(context.Logger, exception.Failure, exception.Message);
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or LgHaltException))
            {
                message = "The LG ThinQ real-time connection failed; retrying, polling meanwhile.";
                LogMqttError(context.Logger, exception);
            }
            finally
            {
                _mqttConnected = false;
                transport.MessageReceived = null;
                transport.Disconnected = null;
            }

            // The timer is armed before the status is published, so whoever reacts to the status (a test clock) finds it.
            var retry = Task.Delay(Backoff(_options.ReconnectInitial, _options.ReconnectMax, failures++), context.Time, cancellationToken);
            context.SetStatus(IntegrationStatus.Degraded, message);
            await retry.ConfigureAwait(false);
        }
    }

    private async Task<string> WaitForLossAsync(IConnectorContext context, ILgMqttTransport transport, Task<string> lost, CancellationToken cancellationToken)
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

    /// <summary>After every (re)connection: event subscriptions again (unless just done) and a resync of stale devices.</summary>
    private async Task OnReconnectedAsync(IConnectorContext context, LgApiClient api, CancellationToken cancellationToken)
    {
        if (Interlocked.Read(ref _connections) > 1 && context.Time.GetUtcNow() - _memory.LastEventSubscribe >= _options.ResubscribeMinInterval)
        {
            await SubscribeAllAsync(context, api, push: false, cancellationToken).ConfigureAwait(false);
        }

        await PollStaleAsync(context, api, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The stored MQTT credentials, or new ones (RSA-2048 key + CSR → <c>POST client/certificate</c>) when there are none,
    /// they expire soon, or the broker rejected them twice. One request at a time and at most one a minute per integration;
    /// the new key, certificate and topic are saved together before they are used.
    /// </summary>
    private async Task<LgMqttCredentials> CredentialsAsync(IConnectorContext context, LgApiClient api, bool reissue, CancellationToken cancellationToken)
    {
        await _memory.CertificateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = LgMqttCredentials.FromJson(await _instance.Secrets.GetAsync(LgSecrets.MqttCredentials, cancellationToken).ConfigureAwait(false));
            var now = context.Time.GetUtcNow();
            if (stored is not null && !reissue && stored.NotAfter() is { } notAfter && notAfter - now > _options.CertificateRenewBefore)
            {
                return stored;
            }

            var (key, csr) = LgCsr.Create();

            // Every attempt waits for its minute — a retry after a timeout or a server error too: LG may have issued (and
            // counted) the certificate whose answer never arrived.
            var answer = await CallAsync(
                    context,
                    async ct =>
                    {
                        var wait = _memory.LastCertificateRequest + _options.CertificateMinInterval - context.Time.GetUtcNow();
                        if (wait > TimeSpan.Zero)
                        {
                            await Task.Delay(wait, context.Time, ct).ConfigureAwait(false);
                        }

                        _memory.LastCertificateRequest = context.Time.GetUtcNow();
                        try
                        {
                            return await api.IssueCertificateAsync(csr, ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            // Counted from when the attempt ended (it may have waited for the call budget first).
                            _memory.LastCertificateRequest = context.Time.GetUtcNow();
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var result = answer.Get("result") ?? answer;
            var certificate = result.Str("certificatePem");
            var topic = result.Array("subscriptions").Select(s => s.ValueKind == JsonValueKind.String ? s.GetString() : null).FirstOrDefault(s => s is { Length: > 0 });
            if (certificate is null || topic is null)
            {
                throw new LgMqttSetupException("LG ThinQ did not issue an MQTT client certificate");
            }

            var issued = new LgMqttCredentials(key, certificate, topic, context.Time.GetUtcNow());
            await _instance.Secrets.SetAsync(LgSecrets.MqttCredentials, issued.ToJson(), cancellationToken).ConfigureAwait(false);
            LogCertificateIssued(context.Logger, stored is null ? "none stored" : reissue ? "rejected by the broker" : "expiring");
            return issued;
        }
        finally
        {
            _memory.CertificateGate.Release();
        }
    }

    /// <summary><c>mqtts://host:8883</c> → (host, port); <c>null</c> host when unusable.</summary>
    internal static (string? Host, int Port) ParseRoute(string? server)
    {
        if (string.IsNullOrWhiteSpace(server))
        {
            return (null, 8883);
        }

        var text = server.Contains("://", StringComparison.Ordinal) ? server : "mqtts://" + server;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? (uri.Host, uri.IsDefaultPort || uri.Port <= 0 ? 8883 : uri.Port)
            : (null, 8883);
    }

    // ---- messages -----------------------------------------------------------------------------------------------------

    private async Task ProcessAsync(IConnectorContext context, ChannelReader<LgMqttMessage> inbox, CancellationToken cancellationToken)
    {
        await foreach (var message in inbox.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                Handle(context, message);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One bad message (or a mapping bug it exposes) never stops the integration.
                LogMessageFailed(context.Logger, exception.GetType().Name);
            }
        }
    }

    private void Handle(IConnectorContext context, LgMqttMessage message)
    {
        using var document = JsonDocument.Parse(message.Payload);
        var root = document.RootElement;
        var pushType = root.Str("pushType");
        var deviceId = root.Str("deviceId");
        if (pushType is "DEVICE_REGISTERED" or "DEVICE_UNREGISTERED" or "DEVICE_ALIAS_CHANGED")
        {
            RequestRediscovery();
            return;
        }

        LgDeviceModel? device;
        lock (_lock)
        {
            device = deviceId is null ? null : _devices.GetValueOrDefault(deviceId);
        }

        if (device is null)
        {
            LogUnknownDevice(context.Logger, pushType ?? "-");
            return;
        }

        switch (pushType)
        {
            case "DEVICE_STATUS" when root.Get("report") is { ValueKind: JsonValueKind.Object } report:
                Report(context, device, _memory.StatusOf(device.DeviceId).Merge(report));
                break;
            case "DEVICE_PUSH" when root.Str("pushCode") is { Length: > 0 } code:
                if (device.NotificationEndpoint(code) is { } endpoint)
                {
                    context.ReportEvent(new EventReport(
                        device.DeviceId,
                        endpoint,
                        CapabilityIds.Generic,
                        LgDeviceModel.NotificationAttribute,
                        code.ToLowerInvariant(),
                        null,
                        null));
                }

                SetAvailability(context, device.DeviceId, Availability.Online);
                break;
        }
    }

    private void Report(IConnectorContext context, LgDeviceModel device, JsonElement status)
    {
        foreach (var reading in device.Read(status))
        {
            context.ReportState(new StateReport(device.DeviceId, reading.Endpoint, reading.Capability, reading.Attribute, reading.Value, null));
        }

        SetAvailability(context, device.DeviceId, Availability.Online);
    }

    private void SetAvailability(IConnectorContext context, string deviceId, Availability availability)
    {
        lock (_lock)
        {
            if (_availability.TryGetValue(deviceId, out var current) && current == availability)
            {
                return;
            }

            _availability[deviceId] = availability;
        }

        context.ReportAvailability(deviceId, availability, null);
    }

    // ---- energy -------------------------------------------------------------------------------------------------------

    private async Task EnergyLoopAsync(IConnectorContext context, LgApiClient api, CancellationToken cancellationToken)
    {
        if (!_settings.EnergyEnabled)
        {
            // Every loop runs until the session stops (the first to end stops the others).
            await Task.Delay(Timeout.InfiniteTimeSpan, context.Time, cancellationToken).ConfigureAwait(false);
            return;
        }

        var timeOfDay = _options.EnergyTimeOfDay ?? (_memory.EnergyTimeOfDay ??= LgEnergy.RandomTimeOfDay(_random));
        while (true)
        {
            var next = LgEnergy.NextRun(context.Time.GetUtcNow(), _settings.TimeZone, timeOfDay);
            LogEnergyScheduled(context.Logger, next);
            var delay = Task.Delay(next - context.Time.GetUtcNow(), context.Time, cancellationToken);
            NextEnergyRun = next;
            await delay.ConfigureAwait(false);
            await RunEnergyAsync(context, api, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One nightly run: for each energy property, DAILY since the first of last month and MONTHLY last and this month.</summary>
    internal async Task RunEnergyAsync(IConnectorContext context, LgApiClient api, CancellationToken cancellationToken)
    {
        var query = LgEnergy.Query(context.Time.GetUtcNow(), _settings.TimeZone);
        var windows = new List<EnergyWindowReport>();
        foreach (var device in Devices)
        {
            foreach (var (endpoint, property) in device.Energy)
            {
                var daily = await UsageAsync(context, api, device, property, LgEnergy.Daily, LgDates.Day(query.DailyFrom), LgDates.Day(query.DailyTo), cancellationToken)
                    .ConfigureAwait(false);
                if (LgEnergy.IsUndated(daily, property, LgEnergy.Daily, query.DailyFrom, query.DailyTo))
                {
                    // LG's answer has no date we can read per entry: ask for yesterday alone (summed, like LG's SDK). The
                    // months are left out: spread over the days, they would overwrite what earlier nights reported.
                    LogEnergyUndated(context.Logger, device.DeviceId, property);
                    var yesterday = await UsageAsync(context, api, device, property, LgEnergy.Daily, LgDates.Day(query.DailyTo), LgDates.Day(query.DailyTo), cancellationToken)
                        .ConfigureAwait(false);
                    if (LgEnergy.Total(yesterday, property) is { } wh)
                    {
                        windows.Add(LgEnergy.Day(device.DeviceId, endpoint, query.DailyTo, wh, _settings.TimeZone));
                    }

                    continue;
                }

                var monthly = await UsageAsync(context, api, device, property, LgEnergy.Monthly, LgDates.Month(query.MonthlyFrom), LgDates.Month(query.MonthlyTo), cancellationToken)
                    .ConfigureAwait(false);
                windows.AddRange(LgEnergy.Windows(device.DeviceId, endpoint, property, daily, monthly, query, _settings.TimeZone));
            }
        }

        if (windows.Count > 0)
        {
            try
            {
                await context.ReportEnergyWindowsAsync(windows, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The next night reports the same span again.
                LogEnergyFailed(context.Logger, exception.GetType().Name);
            }
        }

        Interlocked.Increment(ref _energyRuns);
        LogEnergyDone(context.Logger, windows.Count);
    }

    private async Task<JsonElement?> UsageAsync(
        IConnectorContext context,
        LgApiClient api,
        LgDeviceModel device,
        string property,
        string period,
        string from,
        string to,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CallAsync(context, ct => api.GetEnergyUsageAsync(device.DeviceId, property, period, from, to, ct), cancellationToken).ConfigureAwait(false);
        }
        catch (LgApiException exception) when (exception.Kind is LgErrorKind.Device or LgErrorKind.Other or LgErrorKind.CommandRejected)
        {
            LogEnergyUnavailable(context.Logger, device.DeviceId, period, LgErrors.NameOf(exception.Code));
            return null;
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    /// <summary><paramref name="initial"/> doubling per failure up to <paramref name="max"/>, with ± jitter.</summary>
    private TimeSpan Backoff(TimeSpan initial, TimeSpan max, int failures)
    {
        var exponential = initial.Ticks * Math.Pow(2, Math.Min(failures, 30));
        var baseTicks = Math.Min(exponential, max.Ticks);
        double factor;
        lock (_lock)
        {
            factor = 1 + (_options.Jitter * ((2 * _random.NextDouble()) - 1));
        }

        return TimeSpan.FromTicks((long)Math.Min(baseTicks * factor, max.Ticks));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "LG ThinQ: {Count} of {Listed} appliances in the inventory")]
    private static partial void LogDiscovered(ILogger logger, int count, int listed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "LG ThinQ appliance {DeviceId} ({DeviceType}) left out: its profile is not available ({Error})")]
    private static partial void LogProfileFailed(ILogger logger, string deviceId, string deviceType, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "LG ThinQ inventory rejected ({Reason}); reporting appliances one by one")]
    private static partial void LogInventoryRejected(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "LG ThinQ could not describe {Count} listed appliances for now; appliances are updated, none is orphaned this time")]
    private static partial void LogInventoryIncomplete(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "LG ThinQ appliance {DeviceId} was rejected by the inventory (skipped): {Reason}")]
    private static partial void LogDeviceRejected(ILogger logger, string deviceId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "LG ThinQ state of {DeviceId} not available: {Reason}")]
    private static partial void LogPollFailed(ILogger logger, string deviceId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "LG ThinQ subscription for {Target} not possible ({Error})")]
    private static partial void LogSubscribeFailed(ILogger logger, string target, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "LG ThinQ real-time connection to {Host}:{Port} established")]
    private static partial void LogMqttConnected(ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "LG ThinQ real-time connection lost: {Reason}")]
    private static partial void LogMqttLost(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "LG ThinQ real-time connection failed ({Failure}): {Reason}")]
    private static partial void LogMqttFailed(ILogger logger, LgMqttFailure failure, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "LG ThinQ real-time connection failed")]
    private static partial void LogMqttError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "LG ThinQ could not set up the MQTT client: {Reason}")]
    private static partial void LogMqttSetupFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "LG ThinQ stored MQTT client certificate is unusable; requesting a new one")]
    private static partial void LogCredentialsUnusable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "LG ThinQ MQTT client certificate issued ({Reason})")]
    private static partial void LogCertificateIssued(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "LG ThinQ could not handle a message ({Error})")]
    private static partial void LogMessageFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "LG ThinQ {PushType} message for an appliance not in the inventory (ignored)")]
    private static partial void LogUnknownDevice(ILogger logger, string pushType);

    [LoggerMessage(Level = LogLevel.Information, Message = "LG ThinQ command for {DeviceId} rejected: {ErrorName} ({Code})")]
    private static partial void LogCommandRejected(ILogger logger, string deviceId, string errorName, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "LG ThinQ integration paused ({Status}): {Reason}")]
    private static partial void LogHalted(ILogger logger, IntegrationStatus status, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "LG ThinQ energy: next run at {Next}")]
    private static partial void LogEnergyScheduled(ILogger logger, DateTimeOffset next);

    [LoggerMessage(Level = LogLevel.Information, Message = "LG ThinQ energy: {Windows} windows reported")]
    private static partial void LogEnergyDone(ILogger logger, int windows);

    [LoggerMessage(Level = LogLevel.Warning, Message = "LG ThinQ energy windows could not be saved ({Error}); the next run reports them again")]
    private static partial void LogEnergyFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "LG ThinQ energy of {DeviceId} ({Property}): the daily answer has no readable dates; reporting yesterday alone")]
    private static partial void LogEnergyUndated(ILogger logger, string deviceId, string property);

    [LoggerMessage(Level = LogLevel.Information, Message = "LG ThinQ energy of {DeviceId} ({Period}) not available ({Error})")]
    private static partial void LogEnergyUnavailable(ILogger logger, string deviceId, string period, string error);
}

/// <summary>LG answered an MQTT setup request (client certificate, route) with nothing usable: retried, polling meanwhile.</summary>
internal sealed class LgMqttSetupException(string message) : Exception(message);

/// <summary>The session cannot go on: set <see cref="Status"/> and wait (for <see cref="RetryAfter"/>, or until restarted).</summary>
internal sealed class LgHaltException(IntegrationStatus status, string message, TimeSpan? retryAfter) : Exception(message)
{
    public IntegrationStatus Status { get; } = status;

    public TimeSpan? RetryAfter { get; } = retryAfter;
}

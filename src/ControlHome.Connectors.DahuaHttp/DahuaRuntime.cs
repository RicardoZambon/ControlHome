using System.Globalization;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.DahuaHttp.Model;
using ControlHome.Connectors.DahuaHttp.Protocol;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using Microsoft.Extensions.Logging;

namespace ControlHome.Connectors.DahuaHttp;

/// <summary>
/// One Dahua-family device (camera, recorder or access terminal). Startup identifies it and builds the inventory
/// (<see cref="DahuaInventory"/>: recorder channels become camera children), then keeps <b>one</b> long-lived event stream
/// open (<c>eventManager.cgi?action=attach&amp;codes=[All]&amp;heartbeat=5</c>) and filters the events locally
/// (<see cref="DahuaEventMapper"/>). After every (re)connect the current state is read back (active motion, alarm inputs,
/// video loss, door status), so nothing missed while the stream was down stays stale. A stream that ends, breaks or stays
/// quiet past <see cref="DahuaRuntimeOptions.HeartbeatTimeout"/> is reopened after an exponential back-off with jitter;
/// while the device cannot be reached its devices are <see cref="Availability.Offline"/>.
/// <para>
/// A refused login (401) sets <see cref="IntegrationStatus.NeedsReauth"/> and waits
/// <see cref="DahuaRuntimeOptions.AuthRetryDelay"/> (30 min) before trying once more: devices lock a client out after a
/// few failed logins, so wrong credentials are never retried quickly.
/// </para>
/// <para>
/// Commands: <c>doorOpener.open</c> (safety-critical) releases the door with one request that is never retried or
/// hedged, behind an idempotency key; <c>camera.snapshot</c> fetches a still. Stills come from <c>snapshot.cgi</c>
/// (a camera's channel numbering is probed once: 1-based, else 0-based) and live streams are
/// <c>rtsp://…/cam/realmonitor?channel=N&amp;subtype=0|1</c>, handed only to the host's restreamer.
/// </para>
/// </summary>
internal sealed partial class DahuaRuntime : IConnectorRuntime, ICameraSnapshotSource, ICameraStreamSource
{
    private readonly IntegrationInstance _instance;
    private readonly DahuaSettings _settings;
    private readonly DahuaClientFactory _clients;
    private readonly DahuaRuntimeOptions _options;
    private readonly ReconnectBackoff _backoff;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _usedKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Device, string Endpoint), long> _pulses = [];
    private readonly HashSet<string> _videoLost = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unknownCodes = new(StringComparer.Ordinal);
    private IConnectorContext? _context;
    private DahuaHttpClient? _client;
    private DahuaInventory? _inventory;
    private string? _password;
    private int? _snapshotBase;
    private long _pulseGeneration;
    private long _streamsOpened;
    private long _eventsHandled;

    internal DahuaRuntime(IntegrationInstance instance, DahuaSettings settings, DahuaClientFactory clients, DahuaRuntimeOptions options)
    {
        _instance = instance;
        _settings = settings;
        _clients = clients;
        _options = options;
        _backoff = new ReconnectBackoff(options.ReconnectInitial, options.ReconnectMax, options.ReconnectJitter);
    }

    public DahuaSettings Settings => _settings;

    /// <summary>Event streams opened so far (tests wait on it after a drop).</summary>
    internal long StreamsOpened => Interlocked.Read(ref _streamsOpened);

    /// <summary>Events handled so far (tests wait on it).</summary>
    internal long EventsHandled => Interlocked.Read(ref _eventsHandled);

    internal DahuaInventory? Inventory
    {
        get
        {
            lock (_lock)
            {
                return _inventory;
            }
        }
    }

    public async Task RunAsync(IConnectorContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_lock)
        {
            if (_context is not null)
            {
                throw new InvalidOperationException("This device integration is already running.");
            }

            _context = context;
        }

        try
        {
            context.SetStatus(IntegrationStatus.Connecting, null);
            var password = await _instance.Secrets.GetAsync(DahuaSettings.PasswordField, cancellationToken).ConfigureAwait(false);
            using var client = _clients(new DahuaConnection(_settings.BaseUri, _settings.Username, password, context.Time));
            lock (_lock)
            {
                _client = client;
                _password = password;
            }

            await SuperviseAsync(context, client, cancellationToken).ConfigureAwait(false);
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
                _client = null;
                _inventory = null;
                _password = null;
                _pulses.Clear();
                _videoLost.Clear();
            }
        }
    }

    public async Task<CommandOutcome> ExecuteAsync(DeviceCommandBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        IConnectorContext context;
        DahuaHttpClient client;
        DahuaInventory inventory;
        lock (_lock)
        {
            if (_context is null || _client is null || _inventory is null)
            {
                return CommandOutcome.Reject(CommandRejectionCodes.Unavailable, "The device integration is not running or not connected yet.");
            }

            (context, client, inventory) = (_context, _client, _inventory);
        }

        if (batch.Commands.Count == 0)
        {
            return CommandOutcome.Reject(CommandRejectionCodes.InvalidArguments, "The batch has no commands.");
        }

        if (!inventory.Devices.Any(d => d.ExternalId == batch.ExternalDeviceId))
        {
            return CommandOutcome.Reject(CommandRejectionCodes.UnknownDevice, "No such device.");
        }

        var safetyCritical = false;
        foreach (var command in batch.Commands)
        {
            if (!inventory.HasEndpoint(batch.ExternalDeviceId, command.EndpointKey))
            {
                return CommandOutcome.Reject(CommandRejectionCodes.UnknownEndpoint, "The device has no such endpoint.");
            }

            var supported = (command.CapabilityId, command.Command) switch
            {
                (CapabilityIds.DoorOpener, "open") => inventory.DoorOf(batch.ExternalDeviceId, command.EndpointKey) is not null,
                (CapabilityIds.Camera, "snapshot") => inventory.ChannelOf(batch.ExternalDeviceId, command.EndpointKey) is not null,
                _ => false,
            };
            if (!supported)
            {
                return CommandOutcome.Reject(CommandRejectionCodes.UnsupportedCommand, "The device does not support this command.");
            }

            safetyCritical |= command.CapabilityId == CapabilityIds.DoorOpener;
        }

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
            else if (!_usedKeys.TryAdd(batch.IdempotencyKey, now))
            {
                return CommandOutcome.Reject(CommandRejectionCodes.Duplicate, "This command was already executed.");
            }
        }

        // At-most-once: the key stays used whatever happens from here on, and nothing below is ever retried.
        foreach (var command in batch.Commands)
        {
            var outcome = command.CapabilityId == CapabilityIds.DoorOpener
                ? await OpenDoorAsync(context, client, inventory.DoorOf(batch.ExternalDeviceId, command.EndpointKey)!.Value, cancellationToken).ConfigureAwait(false)
                : await TakeSnapshotAsync(batch.ExternalDeviceId, command.EndpointKey, cancellationToken).ConfigureAwait(false);
            if (outcome is CommandOutcome.Rejected)
            {
                return outcome;
            }
        }

        return CommandOutcome.CompletedOutcome;
    }

    public async Task RefreshAsync(string externalDeviceId, CancellationToken cancellationToken)
    {
        IConnectorContext? context;
        DahuaHttpClient? client;
        DahuaInventory? inventory;
        lock (_lock)
        {
            (context, client, inventory) = (_context, _client, _inventory);
        }

        if (context is not null && client is not null && inventory is not null && inventory.Kind != DahuaDeviceKind.XpeIntercom)
        {
            await ResyncAsync(context, client, inventory, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<CameraSnapshot?> GetSnapshotAsync(string externalDeviceId, string endpointKey, CancellationToken cancellationToken)
    {
        IConnectorContext? context;
        DahuaHttpClient? client;
        int? channel;
        int? knownBase;
        bool standalone;
        lock (_lock)
        {
            context = _context;
            client = _client;
            channel = _inventory?.ChannelOf(externalDeviceId, endpointKey);
            knownBase = _snapshotBase;
            standalone = _inventory?.Kind == DahuaDeviceKind.Camera;
        }

        if (context is null || client is null || channel is not { } number)
        {
            return null;
        }

        // Cameras number their single channel 1 or 0 depending on the firmware: probe once, then keep it. Recorder channels
        // are always 1-based (a failing channel there means no camera, never "try the neighbour").
        int[] candidates = knownBase is { } b ? [number - 1 + b] : standalone ? [number, number - 1] : [number];
        foreach (var candidate in candidates.Where(c => c >= 0))
        {
            try
            {
                var (contentType, data) = await client.GetSnapshotAsync(candidate, cancellationToken).ConfigureAwait(false);
                lock (_lock)
                {
                    _snapshotBase ??= candidate == number ? 1 : 0;
                }

                return new CameraSnapshot(contentType, data, context.Time.GetUtcNow());
            }
            catch (DahuaHttpException exception) when (exception.Failure is DahuaFailure.BadRequest or DahuaFailure.NotFound
                or DahuaFailure.ServerError or DahuaFailure.OtherStatus or DahuaFailure.InvalidResponse)
            {
                LogSnapshotRefused(context.Logger, candidate, exception.Failure);
            }
            catch (DahuaHttpException exception)
            {
                LogSnapshotFailed(context.Logger, exception.Failure, exception.Message);
                return null;
            }
        }

        return null;
    }

    public Task<CameraStreamSource?> GetStreamSourceAsync(
        string externalDeviceId,
        string endpointKey,
        CameraStreamQuality quality,
        CancellationToken cancellationToken)
    {
        string? password;
        int? channel;
        lock (_lock)
        {
            password = _password;
            channel = _context is null ? null : _inventory?.ChannelOf(externalDeviceId, endpointKey);
        }

        if (channel is not { } number || password is null)
        {
            return Task.FromResult<CameraStreamSource?>(null);
        }

        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"rtsp://{Uri.EscapeDataString(_settings.Username)}:{Uri.EscapeDataString(password)}@{_settings.HostForUri}:{_settings.RtspPort}/cam/realmonitor?channel={number}&subtype={(quality == CameraStreamQuality.Sub ? 1 : 0)}");
        return Task.FromResult<CameraStreamSource?>(new CameraStreamSource(url));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---- connection loop --------------------------------------------------------------------------------------------------

    private async Task SuperviseAsync(IConnectorContext context, DahuaHttpClient client, CancellationToken cancellationToken)
    {
        var failures = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan delay;
            try
            {
                var inventory = Inventory ?? await DiscoverAsync(context, client, cancellationToken).ConfigureAwait(false);
                if (inventory.Kind == DahuaDeviceKind.XpeIntercom)
                {
                    await client.ProbeAsync(cancellationToken).ConfigureAwait(false);
                    ReportAvailability(context, inventory, Availability.Online, null);
                    context.SetStatus(IntegrationStatus.Ok, null);
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.Time, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var opened = context.Time.GetUtcNow();
                await StreamAsync(context, client, inventory, cancellationToken).ConfigureAwait(false);

                // The device closed the stream (a reboot, a firmware limit): reconnect.
                failures = context.Time.GetUtcNow() - opened >= _options.StableConnection ? 0 : failures;
                LogStreamEnded(context.Logger, client.Endpoint);
                ReportAvailability(context, inventory, Availability.Unknown, "The event stream ended; reconnecting.");
                context.SetStatus(IntegrationStatus.Degraded, $"The event stream of the device at {client.Endpoint} ended; reconnecting.");
                delay = _backoff.Next(failures++);
            }
            catch (DahuaHttpException exception) when (exception.Failure == DahuaFailure.Unauthorized)
            {
                // Never retried quickly: a few failed logins lock this server out of the device.
                LogUnauthorized(context.Logger, client.Endpoint, _options.AuthRetryDelay);
                MarkAll(context, Availability.Unknown, "The device refused the credentials.");
                failures = 0;
                var retry = Task.Delay(_options.AuthRetryDelay, context.Time, cancellationToken);
                context.SetStatus(
                    IntegrationStatus.NeedsReauth,
                    $"The device at {client.Endpoint} refused the user name or password. Update them (reauthorize); ControlHome tries again only every {_options.AuthRetryDelay.TotalMinutes:0} minutes to avoid locking the device.");
                await retry.ConfigureAwait(false);
                continue;
            }
            catch (DahuaHttpException exception)
            {
                LogConnectionFailed(context.Logger, client.Endpoint, exception.Failure, exception.Message);
                var unreachable = exception.Failure is DahuaFailure.Unreachable or DahuaFailure.Timeout;
                MarkAll(context, unreachable ? Availability.Offline : Availability.Unknown, unreachable ? "The device cannot be reached." : null);
                delay = _backoff.Next(failures++);
                var status = exception.Failure is DahuaFailure.NotFound or DahuaFailure.Forbidden or DahuaFailure.InvalidResponse
                    ? IntegrationStatus.Error
                    : IntegrationStatus.Degraded;
                var wait = Task.Delay(delay, context.Time, cancellationToken);
                context.SetStatus(status, $"{exception.Message} Retrying.");
                await wait.ConfigureAwait(false);
                continue;
            }

            // The timer starts before the status is published, so whoever reacts to the status (a test clock) finds it armed.
            await Task.Delay(delay, context.Time, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<DahuaInventory> DiscoverAsync(IConnectorContext context, DahuaHttpClient client, CancellationToken cancellationToken)
    {
        DahuaInventory inventory;
        if (_settings.Kind == DahuaDeviceKind.XpeIntercom)
        {
            inventory = DahuaInventory.Create(new DahuaInventoryInput(
                DahuaDeviceKind.XpeIntercom,
                new DahuaIdentity("XPE 3200 IP", null, null, null, "Intelbras"),
                string.Create(CultureInfo.InvariantCulture, $"xpe-{_instance.Id:N}"),
                [],
                0,
                new Dictionary<int, IReadOnlySet<string>>(),
                _settings.Language));
        }
        else
        {
            var identity = await client.IdentifyAsync(cancellationToken).ConfigureAwait(false);
            var titles = await client.GetChannelTitlesAsync(cancellationToken).ConfigureAwait(false);
            var kind = _settings.Kind == DahuaDeviceKind.Auto ? DahuaKindDetector.Detect(identity, titles.Count) : _settings.Kind;
            List<(int Number, string? Title)> channels = [];
            if (kind == DahuaDeviceKind.Recorder)
            {
                channels = [.. titles.Where(t => _settings.Channels?.Contains(t.Key) ?? true).Select(t => (t.Key, (string?)t.Value))];
                if (titles.Count == 0)
                {
                    LogNoChannels(context.Logger, client.Endpoint);
                }
            }

            var rules = kind is DahuaDeviceKind.Camera or DahuaDeviceKind.Recorder
                ? await client.GetSmartRulesAsync(cancellationToken).ConfigureAwait(false)
                : new Dictionary<int, IReadOnlySet<string>>();
            var alarms = await client.GetAlarmInputCountAsync(cancellationToken).ConfigureAwait(false);
            inventory = DahuaInventory.Create(new DahuaInventoryInput(
                kind,
                identity,
                string.Create(CultureInfo.InvariantCulture, $"dahua-{_instance.Id:N}"),
                channels,
                alarms,
                rules,
                _settings.Language));
            LogIdentified(context.Logger, client.Endpoint, identity.DeviceType, kind, inventory.ImportedChannels);
        }

        await context.SyncInventoryAsync(inventory.Devices, cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            _inventory = inventory;
        }

        var now = context.Time.GetUtcNow();
        foreach (var (deviceId, _) in inventory.Cameras)
        {
            context.ReportState(new StateReport(deviceId, DahuaInventory.MainEndpoint, CapabilityIds.Camera, "streamAvailable", StateValue.True, now));
        }

        if (inventory.Kind == DahuaDeviceKind.Recorder)
        {
            context.ReportState(new StateReport(
                inventory.RootId,
                DahuaInventory.MainEndpoint,
                CapabilityIds.Generic,
                DahuaInventory.ChannelsAttribute,
                StateValue.Number(inventory.ImportedChannels),
                now));
        }

        return inventory;
    }

    /// <summary>One event-stream connection: opens it, reads the current state back, then handles events until it ends.</summary>
    private async Task StreamAsync(IConnectorContext context, DahuaHttpClient client, DahuaInventory inventory, CancellationToken cancellationToken)
    {
        var stream = await client.OpenEventStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            Interlocked.Increment(ref _streamsOpened);
            LogStreamOpened(context.Logger, client.Endpoint);
            ReportAvailability(context, inventory, Availability.Online, null);
            await ResyncAsync(context, client, inventory, cancellationToken).ConfigureAwait(false);
            context.SetStatus(IntegrationStatus.Ok, null);

            await foreach (var part in stream.ReadPartsAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var e in DahuaEventParser.Parse(part))
                {
                    await HandleAsync(context, inventory, e, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _eventsHandled);
                }
            }
        }
    }

    /// <summary>
    /// Reads back what the stream may have missed: active motion / smart-rule / alarm-input events and video loss
    /// (<c>getEventIndexes</c>), and the door status. A failing read is skipped (the next event corrects it); only a refused
    /// login stops the connection.
    /// </summary>
    private async Task ResyncAsync(IConnectorContext context, DahuaHttpClient client, DahuaInventory inventory, CancellationToken cancellationToken)
    {
        var now = context.Time.GetUtcNow();
        var devices = inventory.Devices;
        bool Has(string device, string endpoint) => devices.Any(d => d.ExternalId == device && d.Endpoints.Any(e => e.Key == endpoint));

        async Task<IReadOnlySet<int>?> ActiveAsync(string code)
        {
            try
            {
                return await client.GetActiveEventChannelsAsync(code, cancellationToken).ConfigureAwait(false);
            }
            catch (DahuaHttpException exception) when (exception.Failure != DahuaFailure.Unauthorized)
            {
                LogResyncFailed(context.Logger, code, exception.Failure);
                return null;
            }
        }

        if (inventory.Kind is DahuaDeviceKind.Camera or DahuaDeviceKind.Recorder)
        {
            var motion = await ActiveAsync("VideoMotion").ConfigureAwait(false) ?? new HashSet<int>();
            var intrusion = devices.Any(d => d.Endpoints.Any(e => e.Key == DahuaInventory.IntrusionEndpoint))
                ? await ActiveAsync(DahuaInventory.IntrusionRule).ConfigureAwait(false) ?? new HashSet<int>()
                : new HashSet<int>();
            foreach (var (deviceId, channel) in inventory.Cameras)
            {
                Report(context, deviceId, DahuaInventory.MainEndpoint, CapabilityIds.Motion, "motion", StateValue.Bool(motion.Contains(channel - 1)), now);
                if (Has(deviceId, DahuaInventory.TripwireEndpoint))
                {
                    ClearPulse(deviceId, DahuaInventory.TripwireEndpoint);
                    Report(context, deviceId, DahuaInventory.TripwireEndpoint, CapabilityIds.Motion, "motion", StateValue.False, now);
                }

                if (Has(deviceId, DahuaInventory.IntrusionEndpoint))
                {
                    Report(context, deviceId, DahuaInventory.IntrusionEndpoint, CapabilityIds.Motion, "motion", StateValue.Bool(intrusion.Contains(channel - 1)), now);
                }
            }

            if (inventory.Kind == DahuaDeviceKind.Recorder && await ActiveAsync("VideoLoss").ConfigureAwait(false) is { } lost)
            {
                foreach (var (deviceId, channel) in inventory.Cameras)
                {
                    var noVideo = lost.Contains(channel - 1);
                    lock (_lock)
                    {
                        _ = noVideo ? _videoLost.Add(deviceId) : _videoLost.Remove(deviceId);
                    }

                    context.ReportAvailability(deviceId, noVideo ? Availability.Offline : Availability.Online, noVideo ? "The recorder has no video from this channel." : null);
                }
            }
        }

        var alarmEndpoints = devices.Single(d => d.ExternalId == inventory.RootId).Endpoints
            .Select(e => (e.Key, Input: DahuaInventory.AlarmInputOf(e.Key)))
            .Where(e => e.Input is not null)
            .ToList();
        if (alarmEndpoints.Count > 0 && await ActiveAsync("AlarmLocal").ConfigureAwait(false) is { } alarms)
        {
            foreach (var (key, input) in alarmEndpoints)
            {
                Report(context, inventory.RootId, key, CapabilityIds.Contact, "open", StateValue.Bool(alarms.Contains(input!.Value - 1)), now);
            }
        }

        if (inventory.Kind == DahuaDeviceKind.AccessControl)
        {
            try
            {
                if (await client.GetDoorOpenAsync(1, cancellationToken).ConfigureAwait(false) is { } open)
                {
                    Report(context, inventory.RootId, DahuaInventory.MainEndpoint, CapabilityIds.Contact, "open", StateValue.Bool(open), now);
                }
            }
            catch (DahuaHttpException exception) when (exception.Failure != DahuaFailure.Unauthorized)
            {
                LogResyncFailed(context.Logger, "DoorStatus", exception.Failure);
            }
        }
    }

    private async Task HandleAsync(IConnectorContext context, DahuaInventory inventory, DahuaEvent e, CancellationToken cancellationToken)
    {
        var effects = DahuaEventMapper.Map(e, inventory);
        if (effects.Count == 0)
        {
            bool first;
            lock (_lock)
            {
                first = !DahuaEventMapper.KnownCodes.Contains(e.Code) && _unknownCodes.Count < 256 && _unknownCodes.Add(e.Code);
            }

            if (first)
            {
                LogUnhandledCode(context.Logger, e.Code);
            }

            return;
        }

        var now = context.Time.GetUtcNow();
        foreach (var effect in effects)
        {
            if (effect is not DahuaAvailabilityEffect && !inventory.HasEndpoint(effect.DeviceId, effect.EndpointKey))
            {
                if (inventory.EnsureEndpoint(effect.DeviceId, effect.EndpointKey) is not { } updated)
                {
                    continue;
                }

                try
                {
                    await context.UpsertDeviceAsync(updated, cancellationToken).ConfigureAwait(false);
                    LogEndpointAdded(context.Logger, effect.EndpointKey);
                }
                catch (ArgumentException exception)
                {
                    LogUpsertRejected(context.Logger, exception, effect.EndpointKey);
                    continue;
                }
            }

            switch (effect)
            {
                case DahuaStateEffect state:
                    if (state.Pulse)
                    {
                        StartPulse(context, state, cancellationToken);
                    }
                    else
                    {
                        if (state.CapabilityId == CapabilityIds.Motion)
                        {
                            ClearPulse(state.DeviceId, state.EndpointKey);
                        }

                        Report(context, state.DeviceId, state.EndpointKey, state.CapabilityId, state.AttributeKey, state.Value, now);
                    }

                    break;
                case DahuaMomentaryEffect momentary:
                    context.ReportEvent(new EventReport(
                        momentary.DeviceId,
                        momentary.EndpointKey,
                        momentary.CapabilityId,
                        momentary.AttributeKey,
                        momentary.Event,
                        momentary.Data,
                        now));
                    break;
                case DahuaAvailabilityEffect availability:
                    lock (_lock)
                    {
                        _ = availability.Availability == Availability.Offline ? _videoLost.Add(availability.DeviceId) : _videoLost.Remove(availability.DeviceId);
                    }

                    context.ReportAvailability(availability.DeviceId, availability.Availability, availability.Reason);
                    break;
            }
        }
    }

    // ---- pulses (instantaneous events) ------------------------------------------------------------------------------------

    private void StartPulse(IConnectorContext context, DahuaStateEffect state, CancellationToken cancellationToken)
    {
        long generation;
        lock (_lock)
        {
            generation = ++_pulseGeneration;
            _pulses[(state.DeviceId, state.EndpointKey)] = generation;
        }

        Report(context, state.DeviceId, state.EndpointKey, state.CapabilityId, state.AttributeKey, StateValue.True, context.Time.GetUtcNow());
        _ = EndPulseAsync(context, state, generation, cancellationToken);
    }

    private async Task EndPulseAsync(IConnectorContext context, DahuaStateEffect state, long generation, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_options.PulseHold, context.Time, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_lock)
        {
            if (!_pulses.TryGetValue((state.DeviceId, state.EndpointKey), out var current) || current != generation || _context != context)
            {
                return;
            }

            _pulses.Remove((state.DeviceId, state.EndpointKey));
        }

        Report(context, state.DeviceId, state.EndpointKey, state.CapabilityId, state.AttributeKey, StateValue.False, context.Time.GetUtcNow());
    }

    private void ClearPulse(string deviceId, string endpointKey)
    {
        lock (_lock)
        {
            _pulses.Remove((deviceId, endpointKey));
        }
    }

    // ---- commands ---------------------------------------------------------------------------------------------------------

    private async Task<CommandOutcome> OpenDoorAsync(IConnectorContext context, DahuaHttpClient client, int door, CancellationToken cancellationToken)
    {
        try
        {
            if (_settings.Kind == DahuaDeviceKind.XpeIntercom)
            {
                await client.OpenXpeRelayAsync(door, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await client.OpenDoorAsync(door, _settings.DoorUserId, cancellationToken).ConfigureAwait(false);
            }

            LogDoorReleased(context.Logger, door);
            return CommandOutcome.CompletedOutcome;
        }
        catch (DahuaHttpException exception)
        {
            LogDoorFailed(context.Logger, door, exception.Failure);
            switch (exception.Failure)
            {
                case DahuaFailure.Unauthorized:
                    context.SetStatus(IntegrationStatus.NeedsReauth, $"The device at {client.Endpoint} refused the user name or password. Update them (reauthorize).");
                    return CommandOutcome.Reject(CommandRejectionCodes.Refused, "The device refused the credentials; the door was not released.");
                case DahuaFailure.Timeout:
                    return CommandOutcome.Reject("timeout", "The device did not answer in time; the door may or may not have opened. It was not retried.");
                case DahuaFailure.Unreachable:
                    return CommandOutcome.Reject(CommandRejectionCodes.Unavailable, "The device cannot be reached; the door was not released.");
                default:
                    return CommandOutcome.Reject(CommandRejectionCodes.Refused, $"The device refused the door release ({exception.Failure}).");
            }
        }
    }

    private async Task<CommandOutcome> TakeSnapshotAsync(string externalDeviceId, string endpointKey, CancellationToken cancellationToken) =>
        await GetSnapshotAsync(externalDeviceId, endpointKey, cancellationToken).ConfigureAwait(false) is null
            ? CommandOutcome.Reject(CommandRejectionCodes.Unavailable, "The camera did not deliver a picture.")
            : CommandOutcome.CompletedOutcome;

    // ---- helpers ----------------------------------------------------------------------------------------------------------

    private static void Report(IConnectorContext context, string device, string endpoint, string capability, string attribute, StateValue value, DateTimeOffset at) =>
        context.ReportState(new StateReport(device, endpoint, capability, attribute, value, at));

    /// <summary>Every device's availability; recorder channels without video stay offline while the recorder is online.</summary>
    private void ReportAvailability(IConnectorContext context, DahuaInventory inventory, Availability availability, string? reason)
    {
        foreach (var device in inventory.Devices)
        {
            bool lost;
            lock (_lock)
            {
                lost = _videoLost.Contains(device.ExternalId);
            }

            context.ReportAvailability(
                device.ExternalId,
                availability == Availability.Online && lost ? Availability.Offline : availability,
                availability == Availability.Online && lost ? "The recorder has no video from this channel." : reason);
        }
    }

    private void MarkAll(IConnectorContext context, Availability availability, string? reason)
    {
        if (Inventory is { } inventory)
        {
            foreach (var device in inventory.Devices)
            {
                context.ReportAvailability(device.ExternalId, availability, reason);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Device at {Endpoint} is a {DeviceType} ({Kind}, {Channels} channel(s) imported)")]
    private static partial void LogIdentified(ILogger logger, string endpoint, string deviceType, DahuaDeviceKind kind, int channels);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The recorder at {Endpoint} reported no video channels (no ChannelTitle table)")]
    private static partial void LogNoChannels(ILogger logger, string endpoint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Event stream of the device at {Endpoint} opened")]
    private static partial void LogStreamOpened(ILogger logger, string endpoint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Event stream of the device at {Endpoint} ended")]
    private static partial void LogStreamEnded(ILogger logger, string endpoint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The device at {Endpoint} refused the credentials; next try in {Delay}")]
    private static partial void LogUnauthorized(ILogger logger, string endpoint, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connection to the device at {Endpoint} failed ({Failure}): {Reason}")]
    private static partial void LogConnectionFailed(ILogger logger, string endpoint, DahuaFailure failure, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Reading back {Code} after a reconnect failed ({Failure})")]
    private static partial void LogResyncFailed(ILogger logger, string code, DahuaFailure failure);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignoring event code {Code} (no mapping)")]
    private static partial void LogUnhandledCode(ILogger logger, string code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Added endpoint {Endpoint} announced by an event")]
    private static partial void LogEndpointAdded(ILogger logger, string endpoint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The host rejected the device with the new endpoint {Endpoint}")]
    private static partial void LogUpsertRejected(ILogger logger, Exception exception, string endpoint);

    [LoggerMessage(Level = LogLevel.Debug, Message = "snapshot.cgi refused channel {Channel} ({Failure})")]
    private static partial void LogSnapshotRefused(ILogger logger, int channel, DahuaFailure failure);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Snapshot failed ({Failure}): {Reason}")]
    private static partial void LogSnapshotFailed(ILogger logger, DahuaFailure failure, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Door {Door} released")]
    private static partial void LogDoorReleased(ILogger logger, int door);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Door {Door} release failed ({Failure}); not retried")]
    private static partial void LogDoorFailed(ILogger logger, int door, DahuaFailure failure);
}

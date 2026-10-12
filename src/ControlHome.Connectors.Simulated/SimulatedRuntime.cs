using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;

namespace ControlHome.Connectors.Simulated;

/// <summary>
/// Runs a simulated home: pushes its inventory, reports initial states, then advances the simulation every
/// <see cref="TickInterval"/> of the context's clock and reports what changed. Commands apply after
/// <see cref="CommandLatency"/> and complete (<see cref="CommandOutcome.Completed"/>). Delivery is at-most-once: a batch
/// whose idempotency key was already seen (within <see cref="IdempotencyWindow"/>) is rejected as
/// <see cref="CommandRejectionCodes.Duplicate"/>, and a safety-critical batch without a key is rejected.
/// </summary>
/// <remarks>
/// Deterministic: everything random comes from one <see cref="Random"/> seeded with the configured seed, and all time from
/// <see cref="IConnectorContext.Time"/>, so the same seed and clock produce the same reports.
/// </remarks>
public sealed class SimulatedRuntime : IConnectorRuntime
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan CommandLatency = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan IdempotencyWindow = TimeSpan.FromMinutes(10);

    private readonly Lock _lock = new();
    private readonly SimulatedHome _home;
    private readonly Dictionary<string, DateTimeOffset> _usedKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Device, string Capability, string Attribute), StateValue> _reported = [];
    private IConnectorContext? _context;
    private long _completedTicks;

    internal SimulatedRuntime(SimulatedSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _home = new SimulatedHome(settings);
    }

    public SimulatedSettings Settings => _home.Settings;

    /// <summary>Simulation ticks fully processed (tests wait on it before advancing a fake clock again).</summary>
    internal long CompletedTicks => Interlocked.Read(ref _completedTicks);

    internal SimulatedHome Home => _home;

    public async Task RunAsync(IConnectorContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_lock)
        {
            if (_context is not null)
            {
                throw new InvalidOperationException("This simulated home is already running.");
            }

            _context = context;
            _reported.Clear();
        }

        try
        {
            context.SetStatus(IntegrationStatus.Connecting, null);
            foreach (var device in _home.Devices)
            {
                await context.UpsertDeviceAsync(device.Describe(), cancellationToken).ConfigureAwait(false);
            }

            lock (_lock)
            {
                var now = context.Time.GetUtcNow();
                _home.Start(now);
                foreach (var device in _home.Devices)
                {
                    context.ReportAvailability(device.ExternalId, Availability.Online, null);
                }

                ReportChanges(context, _home.Devices, now, force: false);
            }

            context.SetStatus(IntegrationStatus.Ok, null);

            // The next tick's timer is armed before a tick is processed, so a fake clock advanced right after
            // CompletedTicks moves never misses it.
            var nextTick = Task.Delay(TickInterval, context.Time, cancellationToken);
            while (true)
            {
                await nextTick.ConfigureAwait(false);
                nextTick = Task.Delay(TickInterval, context.Time, cancellationToken);
                lock (_lock)
                {
                    var now = context.Time.GetUtcNow();
                    _home.Tick(now);
                    ReportChanges(context, _home.Devices, now, force: false);
                }

                Interlocked.Increment(ref _completedTicks);
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
            }
        }
    }

    public async Task<CommandOutcome> ExecuteAsync(DeviceCommandBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        IConnectorContext context;
        SimulatedDevice device;
        lock (_lock)
        {
            if (_context is null)
            {
                return CommandOutcome.Reject(CommandRejectionCodes.Unavailable, "The simulated home is not running.");
            }

            context = _context;
            if (_home.Find(batch.ExternalDeviceId) is not { } found)
            {
                return CommandOutcome.Reject(CommandRejectionCodes.UnknownDevice, "No such simulated device.");
            }

            device = found;
            if (batch.Commands.Count == 0)
            {
                return CommandOutcome.Reject(CommandRejectionCodes.InvalidArguments, "The batch has no commands.");
            }

            var safetyCritical = false;
            foreach (var command in batch.Commands)
            {
                if (Check(device, command, out var definition) is { } rejection)
                {
                    return rejection;
                }

                safetyCritical |= definition.IsSafetyCriticalFor(device.DeviceClass);
            }

            var now = context.Time.GetUtcNow();
            foreach (var expired in _usedKeys.Where(k => now - k.Value > IdempotencyWindow).Select(k => k.Key).ToList())
            {
                _usedKeys.Remove(expired);
            }

            if (string.IsNullOrEmpty(batch.IdempotencyKey))
            {
                if (safetyCritical)
                {
                    return CommandOutcome.Reject(
                        CommandRejectionCodes.IdempotencyRequired,
                        "A safety-critical command needs an idempotency key.");
                }
            }
            else if (!_usedKeys.TryAdd(batch.IdempotencyKey, now))
            {
                return CommandOutcome.Reject(CommandRejectionCodes.Duplicate, "This command was already executed.");
            }
        }

        // The key stays used even if the command is cancelled now: never execute it twice.
        await Task.Delay(CommandLatency, context.Time, cancellationToken).ConfigureAwait(false);

        lock (_lock)
        {
            if (!ReferenceEquals(_context, context))
            {
                return CommandOutcome.Reject(CommandRejectionCodes.Unavailable, "The simulated home stopped.");
            }

            var now = context.Time.GetUtcNow();
            foreach (var command in batch.Commands)
            {
                device.Apply(command, now);
            }

            ReportChanges(context, [device], now, force: false);
        }

        return CommandOutcome.CompletedOutcome;
    }

    public Task RefreshAsync(string externalDeviceId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_context is { } context && _home.Find(externalDeviceId) is { } device)
            {
                ReportChanges(context, [device], context.Time.GetUtcNow(), force: true);
            }
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CommandOutcome? Check(SimulatedDevice device, DeviceCommand command, out CommandDefinition definition)
    {
        definition = null!;
        if (!string.Equals(command.EndpointKey, Endpoint.MainKey, StringComparison.Ordinal))
        {
            return CommandOutcome.Reject(CommandRejectionCodes.UnknownEndpoint, "Simulated devices have only the main endpoint.");
        }

        if (!device.Capabilities.Any(c => string.Equals(c.CapabilityId, command.CapabilityId, StringComparison.Ordinal))
            || CapabilityCatalog.Find(command.CapabilityId)?.FindCommand(command.Command) is not { } found)
        {
            return CommandOutcome.Reject(CommandRejectionCodes.UnsupportedCommand, "The device does not support this command.");
        }

        definition = found;
        var validation = CommandValidator.Validate(
            found,
            command.Arguments,
            device.ConstraintsOf(command.CapabilityId),
            device.StateOf(command.CapabilityId));
        return validation.IsValid
            ? null
            : CommandOutcome.Reject(
                CommandRejectionCodes.InvalidArguments,
                string.Join(", ", validation.Errors.Select(e => $"{e.Argument ?? "arguments"}: {e.Kind}")));
    }

    /// <summary>Reports every value that differs from the last reported one (or all of them when <paramref name="force"/>).</summary>
    private void ReportChanges(IConnectorContext context, IEnumerable<SimulatedDevice> devices, DateTimeOffset now, bool force)
    {
        foreach (var device in devices)
        {
            foreach (var (capability, attribute, value) in device.Snapshot())
            {
                var key = (device.ExternalId, capability, attribute);
                if (!force && _reported.TryGetValue(key, out var previous) && previous.Equals(value))
                {
                    continue;
                }

                _reported[key] = value;
                context.ReportState(new StateReport(device.ExternalId, Endpoint.MainKey, capability, attribute, value, now));
            }
        }
    }
}

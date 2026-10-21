using System.Collections.Concurrent;
using System.Text.Json;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Devices;
using ControlHome.Application.Integrations;
using ControlHome.Application.Realtime;
using ControlHome.Application.Runtime;
using ControlHome.Application.Security;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlHome.Infrastructure.Devices;

/// <summary>
/// Runs one connector runtime per enabled integration while this process is the active runtime: creates it from its
/// connector's factory, calls <c>RunAsync</c> and restarts it after a failure with exponential back-off
/// (<see cref="DeviceRuntimeOptions.RestartInitialDelay"/> doubling up to <see cref="DeviceRuntimeOptions.RestartMaxDelay"/>).
/// While a runtime is down its devices are <see cref="Availability.Unknown"/>. Statuses (runtime-reported or its own) are
/// redacted (<see cref="KnownSecrets"/>), saved and pushed to clients. Also the command dispatcher: commands reach a
/// runtime only through <see cref="DispatchAsync"/>, exactly once.
/// <para>
/// Never two runtimes of one integration: a run is fenced the moment its stop is requested (its context and secret store
/// refuse everything), a runtime that does not finish within <see cref="DeviceRuntimeOptions.StopTimeout"/> is kept as
/// "stopping" and its successor starts only once it has finished, and once <see cref="StopAllAsync"/> ran (lease lost,
/// shutdown) nothing starts until <see cref="StartAllAsync"/>.
/// </para>
/// </summary>
public sealed partial class IntegrationSupervisor(
    IServiceScopeFactory scopes,
    ConnectorCatalog connectors,
    StateProcessor processor,
    IDeviceInventory inventory,
    DeviceRegistry registry,
    StateCache cache,
    IntegrationSecretProtector secretProtector,
    IRealtimePublisher publisher,
    IRuntimeStatus runtimeStatus,
    RuntimeClock clock,
    TimeProvider timeProvider,
    KnownSecrets knownSecrets,
    IOptions<DeviceRuntimeOptions> options,
    ILoggerFactory loggerFactory) : ICommandDispatcher, IIntegrationRuntimeControl, IAsyncDisposable
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<IntegrationSupervisor>();
    private readonly ConcurrentDictionary<Guid, RunningIntegration> _running = new();

    /// <summary>Runs that did not finish within the stop timeout (fenced, still running).</summary>
    private readonly ConcurrentDictionary<Guid, RunningIntegration> _stopping = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Whether starts are accepted (guarded by <see cref="_gate"/>): opened by StartAllAsync, closed by StopAllAsync.</summary>
    private bool _accepting;

    /// <summary>The integrations whose runtime did not stop in time and is still running (fenced).</summary>
    public IReadOnlyCollection<Guid> Stopping => [.. _stopping.Keys];

    /// <summary>The integrations with a supervised run loop.</summary>
    public IReadOnlyCollection<Guid> Supervised => [.. _running.Keys];

    public bool IsRunning(Guid integrationId) =>
        _running.TryGetValue(integrationId, out var running) && running.Runtime is not null;

    public async Task<CommandOutcome> DispatchAsync(Guid integrationId, DeviceCommandBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!_running.TryGetValue(integrationId, out var running) || running.Runtime is not { } runtime)
        {
            return CommandOutcome.Reject(CommandRejectionCodes.Unavailable, "The integration is not running.");
        }

        var outcome = await runtime.ExecuteAsync(batch, cancellationToken).ConfigureAwait(false);
        return outcome is CommandOutcome.Rejected rejected
            ? CommandOutcome.Reject(rejected.Code, knownSecrets.Redact(rejected.Message))
            : outcome;
    }

    /// <summary>Starts every enabled integration (registering their secrets for log redaction first).</summary>
    public async Task StartAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _accepting = true;
        }
        finally
        {
            _gate.Release();
        }

        List<(Guid Id, string? Secrets)> integrations;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IControlHomeDb>();
            integrations = (await db.Integrations.AsNoTracking()
                    .Where(i => i.Enabled)
                    .OrderBy(i => i.CreatedAtUtc)
                    .Select(i => new { i.Id, i.SecretsProtected })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                .Select(i => (i.Id, i.SecretsProtected))
                .ToList();
        }

        foreach (var (id, secrets) in integrations)
        {
            try
            {
                secretProtector.Unprotect(secrets);
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
            {
                LogSecretsUnreadable(id);
            }

            await StartAsync(id, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StartAsync(Guid integrationId, CancellationToken cancellationToken)
    {
        if (runtimeStatus.Mode != RuntimeMode.Active)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Checked again under the gate: a start that queued behind StopAllAsync (lease lost, shutdown) must not run
            // after it. The mode alone is not enough: it changes only once the whole runtime has stopped.
            if (!_accepting || runtimeStatus.Mode != RuntimeMode.Active)
            {
                return;
            }

            if (_running.TryRemove(integrationId, out var previous))
            {
                await StopLoopAsync(previous).ConfigureAwait(false);
            }

            if (_stopping.TryGetValue(integrationId, out var stopping))
            {
                // Its previous runtime is still running (fenced): start once it has finished, never next to it.
                stopping.RestartWhenStopped = true;
                LogStartDeferred(integrationId);
                return;
            }

            var running = new RunningIntegration(integrationId);
            _running[integrationId] = running;
            running.Loop = BackgroundWork.Run(() => RunLoopAsync(running));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(Guid integrationId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopping.TryGetValue(integrationId, out var stopping))
            {
                stopping.RestartWhenStopped = false;
            }

            if (_running.TryRemove(integrationId, out var running))
            {
                await StopLoopAsync(running).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemovedAsync(Guid integrationId, CancellationToken cancellationToken)
    {
        await StopAsync(integrationId, cancellationToken).ConfigureAwait(false);
        cache.Remove(registry.RemoveIntegration(integrationId));
    }

    /// <summary>
    /// Stops every runtime (lease lost, shutdown, test reset). Afterwards no integration starts until
    /// <see cref="StartAllAsync"/>, unless <paramref name="keepAccepting"/> (test reset: the runtime stays active).
    /// </summary>
    public async Task StopAllAsync(bool keepAccepting = false)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!keepAccepting)
            {
                _accepting = false;
            }

            foreach (var stopping in _stopping.Values)
            {
                stopping.RestartWhenStopped = false;
            }

            var all = _running.Values.ToList();
            _running.Clear();
            await Task.WhenAll(all.Select(StopLoopAsync)).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops every runtime. Safe to call more than once (the container disposes each registration of it).</summary>
    public async ValueTask DisposeAsync() => await StopAllAsync().ConfigureAwait(false);

    /// <summary>Stops a run loop (under the gate); one that does not finish in time stays fenced in <see cref="_stopping"/>.</summary>
    private async Task StopLoopAsync(RunningIntegration running)
    {
        await running.Stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await running.Loop.WaitAsync(options.Value.StopTimeout, timeProvider).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            LogStopTimedOut(running.IntegrationId, options.Value.StopTimeout.TotalSeconds);
            _stopping[running.IntegrationId] = running;
            _ = running.Loop.ContinueWith(
                    _ => OnStoppedLateAsync(running),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap();
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    /// <summary>A run that did not stop in time has finished: forget it and start its successor if one was asked for.</summary>
    private async Task OnStoppedLateAsync(RunningIntegration running)
    {
        bool restart;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _stopping.TryRemove(new KeyValuePair<Guid, RunningIntegration>(running.IntegrationId, running));
            restart = running.RestartWhenStopped && _accepting;
        }
        finally
        {
            _gate.Release();
        }

        LogStoppedLate(running.IntegrationId);
        if (restart)
        {
            try
            {
                await StartAsync(running.IntegrationId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogRuntimeFailed(running.IntegrationId, ex.GetType().FullName ?? ex.GetType().Name, knownSecrets.Redact(ex.Message));
            }
        }
    }

    private async Task RunLoopAsync(RunningIntegration running)
    {
        var id = running.IntegrationId;
        var stop = running.Stop.Token;
        var delay = options.Value.RestartInitialDelay;
        while (!stop.IsCancellationRequested)
        {
            var started = timeProvider.GetUtcNow();
            string? failure;
            try
            {
                failure = await RunOnceAsync(running, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Loading the integration or creating its runtime failed (e.g. the database is down): retry like a crash.
                failure = ex.GetType().Name;
                LogRuntimeFailed(id, ex.GetType().FullName ?? ex.GetType().Name, knownSecrets.Redact(ex.Message));
            }

            if (failure is null || stop.IsCancellationRequested)
            {
                // A run that failed only after it was asked to stop must not overwrite its successor's status.
                return;
            }

            if (timeProvider.GetUtcNow() - started >= options.Value.RestartResetAfter)
            {
                delay = options.Value.RestartInitialDelay;
            }

            SetStatus(
                running,
                IntegrationStatus.Error,
                $"The connector stopped unexpectedly ({failure}); retrying in {Math.Ceiling(delay.TotalSeconds):0} s.");
            try
            {
                await Task.Delay(delay, timeProvider, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, options.Value.RestartMaxDelay.Ticks));
        }
    }

    /// <summary>
    /// One run of the integration's runtime. Returns <c>null</c> when there is nothing to retry (stopped, deleted,
    /// disabled, unknown connector, invalid configuration), else why it failed.
    /// </summary>
    private async Task<string?> RunOnceAsync(RunningIntegration running, CancellationToken stop)
    {
        var id = running.IntegrationId;
        Integration? integration;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IControlHomeDb>();
            integration = await db.Integrations.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id && i.Enabled, stop).ConfigureAwait(false);
        }

        if (integration is null)
        {
            return null;
        }

        if (connectors.Find(integration.ConnectorKey) is not { } factory)
        {
            SetStatus(running, IntegrationStatus.Error, $"The connector '{integration.ConnectorKey}' is not available in this version.");
            return null;
        }

        var secrets = new SecretStore(id, scopes, secretProtector);
        IConnectorRuntime runtime;
        try
        {
            using var config = JsonDocument.Parse(integration.ConfigJson);
            runtime = factory.CreateRuntime(new IntegrationInstance(id, integration.Name, config.RootElement.Clone(), secrets));
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            SetStatus(running, IntegrationStatus.Error, "The integration's configuration is invalid.");
            return null;
        }

        // Exceptions a connector logs are redacted too (the Serilog redactor only rewrites property values).
        var logger = new RedactingLogger(loggerFactory.CreateLogger($"ControlHome.Connectors.{factory.Descriptor.Key}"), knownSecrets);
        var context = new ConnectorContext(id, clock.Time, logger, processor, inventory, (s, m) => SetStatus(running, s, m));

        // Fenced the moment a stop is requested, not when RunAsync returns: a runtime that ignores cancellation may keep
        // running for a while, but nothing it reports, upserts or saves is accepted any more.
        using var fence = stop.Register(() =>
        {
            context.Close();
            secrets.Close();
        });
        SetStatus(running, IntegrationStatus.Connecting, null);
        running.Runtime = runtime;
        try
        {
            await runtime.RunAsync(context, stop).ConfigureAwait(false);
            return stop.IsCancellationRequested ? null : "The connector stopped on its own.";
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return null;
        }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested)
        {
            // It used its context or secret store after its stop was requested: fenced, as it should be.
            return null;
        }
        catch (Exception ex)
        {
            // Never the exception object: its message (and inner exceptions) may quote a secret.
            LogRuntimeFailed(id, ex.GetType().FullName ?? ex.GetType().Name, knownSecrets.Redact(ex.Message));
            return ex.GetType().Name;
        }
        finally
        {
            running.Runtime = null;
            context.Close();
            secrets.Close();
            await DisposeQuietlyAsync(id, runtime).ConfigureAwait(false);

            // Unless a newer run of this integration exists (its devices are its business now).
            if (!_running.TryGetValue(id, out var current) || ReferenceEquals(current, running))
            {
                MarkUnknown(id);
            }
        }
    }

    private async Task DisposeQuietlyAsync(Guid id, IConnectorRuntime runtime)
    {
        try
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogDisposeFailed(id, ex.GetType().Name);
        }
    }

    /// <summary>Every device of the integration becomes Unknown (its runtime is not running).</summary>
    private void MarkUnknown(Guid integrationId)
    {
        foreach (var device in registry.DevicesOf(integrationId))
        {
            processor.ReportAvailability(integrationId, device.ExternalId, Availability.Unknown);
        }
    }

    private void SetStatus(RunningIntegration running, IntegrationStatus status, string? message)
    {
        var redacted = message is null ? null : Truncate(knownSecrets.Redact(message), DeviceLimits.StatusMessageMaxLength);
        lock (running.StatusLock)
        {
            if (running.Status == status && string.Equals(running.StatusMessage, redacted, StringComparison.Ordinal))
            {
                return;
            }

            running.Status = status;
            running.StatusMessage = redacted;

            // Written one after the other, so the database always ends with the latest status.
            running.StatusWrite = running.StatusWrite.ContinueWith(
                _ => SaveStatusAsync(running.IntegrationId, status, redacted),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }

        LogStatus(running.IntegrationId, status);
    }

    private async Task SaveStatusAsync(Guid id, IntegrationStatus status, string? message)
    {
        try
        {
            await using (var scope = scopes.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<IControlHomeDb>();
                var now = timeProvider.GetUtcNow();
                await db.Integrations
                    .Where(i => i.Id == id)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(i => i.Status, status).SetProperty(i => i.StatusMessage, message).SetProperty(i => i.UpdatedAtUtc, now),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await publisher.IntegrationStatusChangedAsync(new IntegrationStatusChangedDto(id, status.ToString(), message), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogStatusSaveFailed(id, ex.GetType().Name);
        }
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    [LoggerMessage(Level = LogLevel.Warning, Message = "Integration {IntegrationId}: the connector failed ({ExceptionType}): {Reason}")]
    private partial void LogRuntimeFailed(Guid integrationId, string exceptionType, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Integration {IntegrationId} status: {Status}")]
    private partial void LogStatus(Guid integrationId, IntegrationStatus status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Integration {IntegrationId}: saving its status failed ({ExceptionType})")]
    private partial void LogStatusSaveFailed(Guid integrationId, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Integration {IntegrationId}: the connector did not stop within {Seconds} s; it is fenced until it does")]
    private partial void LogStopTimedOut(Guid integrationId, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Integration {IntegrationId}: the connector that did not stop in time has finished")]
    private partial void LogStoppedLate(Guid integrationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Integration {IntegrationId}: its previous connector is still stopping; it starts once that has finished")]
    private partial void LogStartDeferred(Guid integrationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Integration {IntegrationId}: disposing the connector failed ({ExceptionType})")]
    private partial void LogDisposeFailed(Guid integrationId, string exceptionType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Integration {IntegrationId}: its secrets cannot be decrypted (was the Data Protection key ring replaced?)")]
    private partial void LogSecretsUnreadable(Guid integrationId);

    private sealed class RunningIntegration(Guid integrationId)
    {
        public Guid IntegrationId { get; } = integrationId;

        public CancellationTokenSource Stop { get; } = new();

        public Task Loop { get; set; } = Task.CompletedTask;

        private IConnectorRuntime? _runtime;

        /// <summary>The runtime while its <c>RunAsync</c> runs (commands go to it), else <c>null</c>.</summary>
        public IConnectorRuntime? Runtime
        {
            get => Volatile.Read(ref _runtime);
            set => Volatile.Write(ref _runtime, value);
        }

        public Lock StatusLock { get; } = new();

        public IntegrationStatus? Status { get; set; }

        public string? StatusMessage { get; set; }

        public Task StatusWrite { get; set; } = Task.CompletedTask;

        /// <summary>For a run that did not stop in time: start the integration again once it has (guarded by the gate).</summary>
        public bool RestartWhenStopped { get; set; }
    }
}

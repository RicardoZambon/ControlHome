using ControlHome.Application.Abstractions;
using ControlHome.Application.Runtime;
using ControlHome.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ControlHome.Infrastructure.Devices;

/// <summary>
/// Brings the device runtime up and down as a whole (the host does it when the lease is won or lost; the test reset in
/// between): devices start <see cref="Availability.Unknown"/>, the registry and the state cache are loaded from the database,
/// then the state processor, the write-behind and every enabled integration start.
/// </summary>
public sealed partial class DeviceRuntime(
    IServiceScopeFactory scopes,
    DeviceRuntimeState state,
    DeviceRegistry registry,
    StateCache cache,
    StateProcessor processor,
    StateWriteBehind writeBehind,
    RealtimeBatcher realtime,
    IntegrationSupervisor supervisor,
    ILogger<DeviceRuntime> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsActive { get; private set; }

    public async Task ActivateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsActive)
            {
                return;
            }

            // Availability buffered by an earlier run (a failed final flush) would override the reset to Unknown below.
            writeBehind.DropPendingAvailability();
            await using (var scope = scopes.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<IControlHomeDb>();

                // Nothing runs yet: every device is Unknown until its connector reports it.
                await db.Devices
                    .Where(d => d.Availability != Availability.Unknown)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.Availability, Availability.Unknown), cancellationToken)
                    .ConfigureAwait(false);
                await registry.LoadAsync(db.Devices, cancellationToken).ConfigureAwait(false);
                await cache.HydrateAsync(db.AttributeStates, cancellationToken).ConfigureAwait(false);
            }

            processor.Start();
            writeBehind.Start();
            state.Mode = RuntimeMode.Active;
            IsActive = true;
            LogActivated(registry.DeviceCount, cache.Count);
            await supervisor.StartAllAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops every integration and the pipeline (a last write-behind flush is attempted).</summary>
    public async Task DeactivateAsync(RuntimeMode next, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (IsActive)
            {
                await supervisor.StopAllAsync().ConfigureAwait(false);
                await processor.StopAsync().ConfigureAwait(false);
                await writeBehind.StopAsync(cancellationToken).ConfigureAwait(false);
                await realtime.FlushAsync().ConfigureAwait(false);
                cache.Invalidate();
                registry.Clear();
                IsActive = false;
            }

            state.Mode = next;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Test reset (<c>POST /api/_test/reset</c>, before the database is emptied): stops every integration and drops
    /// everything in flight; the pipeline keeps running over the now empty inventory.
    /// </summary>
    public async Task ResetAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await supervisor.StopAllAsync(keepAccepting: true).ConfigureAwait(false);
            processor.Clear();
            await processor.WhenIdleAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            writeBehind.Clear();
            realtime.Clear();
            registry.Clear();
            cache.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Device runtime active: {Devices} devices, {States} cached states")]
    private partial void LogActivated(int devices, int states);
}

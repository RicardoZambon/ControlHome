using ControlHome.Application.Runtime;
using ControlHome.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlHome.Infrastructure.Devices;

/// <summary>
/// Decides whether this process runs the device runtime: once the database is ready it tries to take the
/// <see cref="RuntimeLease"/>; with it, the runtime is activated (<see cref="DeviceRuntime.ActivateAsync"/>) and the lease is
/// checked every <see cref="DeviceRuntimeOptions.LeaseCheckInterval"/>; without it the process is a read-only
/// <see cref="RuntimeMode.Standby"/> and retries every <see cref="DeviceRuntimeOptions.LeaseRetryInterval"/>. Losing the lease
/// to another process stops the runtime; a database outage does not (control keeps working, state is buffered).
/// </summary>
public sealed partial class DeviceRuntimeHost(
    DatabaseReadiness readiness,
    RuntimeLease lease,
    DeviceRuntime runtime,
    DeviceRuntimeState state,
    TimeProvider timeProvider,
    IOptions<DeviceRuntimeOptions> options,
    ILogger<DeviceRuntimeHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            LogDisabled();
            return;
        }

        try
        {
            await readiness.WaitUntilReadyAsync(stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                bool acquired;
                try
                {
                    acquired = await lease.TryAcquireAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SqlException or InvalidOperationException)
                {
                    LogLeaseAttemptFailed(ex.GetType().Name);
                    acquired = false;
                }

                if (acquired)
                {
                    await RunActiveAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (state.Mode != RuntimeMode.Standby)
                {
                    LogStandby();
                    state.Mode = RuntimeMode.Standby;
                }

                await Task.Delay(options.Value.LeaseRetryInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await runtime.DeactivateAsync(RuntimeMode.Starting, cancellationToken).ConfigureAwait(false);
        await lease.ReleaseAsync().ConfigureAwait(false);
    }

    private async Task RunActiveAsync(CancellationToken stoppingToken)
    {
        var failed = false;
        try
        {
            await runtime.ActivateAsync(stoppingToken).ConfigureAwait(false);
            while (true)
            {
                await Task.Delay(options.Value.LeaseCheckInterval, timeProvider, stoppingToken).ConfigureAwait(false);
                if (await lease.CheckAsync(stoppingToken).ConfigureAwait(false) == LeaseState.Lost)
                {
                    LogLeaseLost();
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogActivationFailed(ex);
            failed = true;
        }
        finally
        {
            if (!stoppingToken.IsCancellationRequested)
            {
                await runtime.DeactivateAsync(failed ? RuntimeMode.Starting : RuntimeMode.Standby, CancellationToken.None).ConfigureAwait(false);
                await lease.ReleaseAsync().ConfigureAwait(false);
            }
        }

        if (failed)
        {
            await Task.Delay(options.Value.LeaseRetryInterval, timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The device runtime is disabled (DeviceRuntime:Enabled = false)")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Another ControlHome process holds the runtime lease on this database: running as a read-only standby")]
    private partial void LogStandby();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Taking the runtime lease failed ({ExceptionType}); retrying")]
    private partial void LogLeaseAttemptFailed(string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The runtime lease was taken by another process: stopping the device runtime")]
    private partial void LogLeaseLost();

    [LoggerMessage(Level = LogLevel.Error, Message = "Starting the device runtime failed; retrying")]
    private partial void LogActivationFailed(Exception exception);
}

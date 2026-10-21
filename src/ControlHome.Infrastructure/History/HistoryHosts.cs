using ControlHome.Application.Runtime;
using ControlHome.Infrastructure.Devices;
using ControlHome.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlHome.Infrastructure.History;

/// <summary>
/// Runs the <see cref="HistoryRollup"/> every <see cref="HistoryOptions.RollupInterval"/> of the runtime clock, and the
/// <see cref="RetentionPurge"/> every <see cref="HistoryOptions.PurgeInterval"/> — only while this process is the active
/// runtime (a standby never writes). Failures are logged and retried on the next tick.
/// </summary>
public sealed partial class HistoryMaintenanceHost(
    DatabaseReadiness readiness,
    IRuntimeStatus status,
    HistoryRollup rollup,
    RetentionPurge purge,
    RuntimeClock clock,
    IOptions<HistoryOptions> options,
    ILogger<HistoryMaintenanceHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.MaintenanceEnabled)
        {
            return;
        }

        try
        {
            await readiness.WaitUntilReadyAsync(stoppingToken).ConfigureAwait(false);
            await Task.WhenAll(
                    LoopAsync(options.Value.RollupInterval, RollUpAsync, stoppingToken),
                    LoopAsync(options.Value.PurgeInterval, PurgeAsync, stoppingToken))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>Calls <paramref name="work"/> every <paramref name="interval"/> while active (the first run as soon as it is active).</summary>
    private async Task LoopAsync(TimeSpan interval, Func<CancellationToken, Task> work, CancellationToken stoppingToken)
    {
        // Checks often whether the runtime became active; runs the work when it is due.
        var poll = interval < TimeSpan.FromMinutes(1) ? interval : TimeSpan.FromMinutes(1);
        using var timer = new PeriodicTimer(poll, clock.Time);
        DateTimeOffset? lastRun = null;
        do
        {
            if (status.Mode != RuntimeMode.Active)
            {
                lastRun = null;
                continue;
            }

            var now = clock.Time.GetUtcNow();
            if (lastRun is { } last && now - last < interval)
            {
                continue;
            }

            lastRun = now;
            try
            {
                await work(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                LogFailed(work.Method.Name, ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private Task RollUpAsync(CancellationToken cancellationToken) => rollup.RunOnceAsync(cancellationToken);

    private Task PurgeAsync(CancellationToken cancellationToken) => purge.PurgeOnceAsync(cancellationToken);

    [LoggerMessage(Level = LogLevel.Warning, Message = "History maintenance {Work} failed ({ExceptionType}); retrying on the next run")]
    private partial void LogFailed(string work, string exceptionType);
}

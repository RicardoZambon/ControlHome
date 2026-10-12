using ControlHome.Application.Abstractions;
using ControlHome.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ControlHome.Infrastructure.Setup;

/// <summary>
/// Announces the setup code once it is known whether setup is still needed: immediately when no database is configured
/// (setup cannot have happened), otherwise as soon as the database is ready and has no completed setup. When setup is
/// already complete it deletes a stale <c>setup-code.txt</c>.
/// </summary>
public sealed partial class SetupCodeAnnouncer(
    DatabaseReadiness readiness,
    IServiceScopeFactory scopeFactory,
    SetupCodeProvider setupCode,
    ILogger<SetupCodeAnnouncer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (readiness.State == DatabaseState.NotConfigured)
            {
                setupCode.Announce();
            }

            await readiness.WaitUntilReadyAsync(stoppingToken);

            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IControlHomeDb>();
            var completed = await db.Homes.AsNoTracking().AnyAsync(h => h.SetupCompletedAtUtc != null, stoppingToken);
            if (completed)
            {
                setupCode.OnSetupCompleted();
            }
            else
            {
                setupCode.Announce();
            }
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutting down (SqlClient reports a cancelled query as a SqlException, not an OperationCanceledException).
        }
        catch (Exception ex)
        {
            // Never stop the host over this; the operator can still find the code after a restart.
            LogFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not determine whether first-run setup is needed")]
    private partial void LogFailed(Exception exception);
}

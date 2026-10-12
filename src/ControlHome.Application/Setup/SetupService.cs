using ControlHome.Application.Abstractions;
using ControlHome.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Application.Setup;

/// <summary>First-run setup lifecycle. 0.2-A/E: status only; completion lands with Task 0.2-C.</summary>
public sealed class SetupService(IControlHomeDb db, IDatabaseStatus databaseStatus)
{
    /// <summary>Answers even while the database is not ready (then setup counts as not completed).</summary>
    public async Task<SetupStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        var completed = databaseStatus.IsReady
            && await db.Homes.AsNoTracking().AnyAsync(h => h.SetupCompletedAtUtc != null, cancellationToken);

        return new SetupStatusDto(completed, databaseStatus.State, databaseStatus.IsConfigured, AppVersion.Informational);
    }
}

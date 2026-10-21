using Microsoft.EntityFrameworkCore;

namespace ControlHome.Infrastructure.Persistence;

/// <summary>
/// Deletes every row from the app and Identity tables (FK-safe order) and re-seeds the roles, returning the database to its
/// freshly-initialized state. Used only by the <c>Testing</c>-environment endpoint <c>POST /api/_test/reset</c>.
/// Data Protection keys are kept, so the key ring stays valid.
/// </summary>
public sealed class DatabaseReset(ControlHomeDbContext db)
{
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async ct =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            await db.UserTokens.ExecuteDeleteAsync(ct);
            await db.UserLogins.ExecuteDeleteAsync(ct);
            await db.UserClaims.ExecuteDeleteAsync(ct);
            await db.UserRoles.ExecuteDeleteAsync(ct);
            await db.RoleClaims.ExecuteDeleteAsync(ct);
            await db.Users.ExecuteDeleteAsync(ct);
            await db.Roles.ExecuteDeleteAsync(ct);
            await db.AuditLog.ExecuteDeleteAsync(ct);

            // History (no foreign keys to the device model).
            await db.Database.ExecuteSqlRawAsync("DELETE FROM [ch].[StateLog]; DELETE FROM [ch].[DeviceEvent];", ct);
            await db.ShortTermStatistics.ExecuteDeleteAsync(ct);
            await db.HourlyStatistics.ExecuteDeleteAsync(ct);
            await db.RollupProgress.ExecuteDeleteAsync(ct);
            await db.EnergyHourly.ExecuteDeleteAsync(ct);
            await db.EnergySourceSelections.ExecuteDeleteAsync(ct);
            await db.RetentionSettings.ExecuteDeleteAsync(ct);

            // Devices (and everything below them) go with their integration; placements of devices, parent links and rooms do
            // not cascade.
            await db.Placements.ExecuteDeleteAsync(ct);
            await db.Devices.Where(d => d.ParentDeviceId != null).ExecuteUpdateAsync(s => s.SetProperty(d => d.ParentDeviceId, (Guid?)null), ct);
            await db.Integrations.ExecuteDeleteAsync(ct);
            await db.Rooms.ExecuteDeleteAsync(ct);
            await db.Floors.ExecuteDeleteAsync(ct);
            await db.StoredFiles.ExecuteDeleteAsync(ct);
            await db.Homes.ExecuteDeleteAsync(ct);

            await RoleSeeder.SeedAsync(db, ct);
            await transaction.CommitAsync(ct);
        }, cancellationToken);

        db.ChangeTracker.Clear();
    }
}

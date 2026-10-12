using ControlHome.Domain.Auditing;
using ControlHome.Domain.Files;
using ControlHome.Domain.Homes;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Application.Abstractions;

/// <summary>The application's port to the database: DbSets plus SaveChanges (no repository/unit-of-work layers).</summary>
public interface IControlHomeDb
{
    DbSet<Home> Homes { get; }

    DbSet<AuditLogEntry> AuditLog { get; }

    DbSet<Floor> Floors { get; }

    DbSet<Room> Rooms { get; }

    DbSet<StoredFile> StoredFiles { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

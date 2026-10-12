using ControlHome.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Application.Auditing;

/// <summary>Reads the audit log newest first, with keyset paging on the entry id.</summary>
public sealed class AuditQueryService(IControlHomeDb db)
{
    public const int MaxPageSize = 200;

    /// <param name="cursor">The previous page's <see cref="PagedResult{T}.NextCursor"/>; null for the first page.</param>
    /// <param name="take">Page size, clamped to 1..<see cref="MaxPageSize"/>.</param>
    public async Task<PagedResult<AuditEntryDto>> ListAsync(long? cursor, int take, CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, MaxPageSize);

        var query = db.AuditLog.AsNoTracking();
        if (cursor is { } before)
        {
            query = query.Where(e => e.Id < before);
        }

        // One extra row tells whether another page exists.
        var rows = await query.OrderByDescending(e => e.Id).Take(take + 1).ToListAsync(cancellationToken);
        var items = rows.Take(take)
            .Select(e => new AuditEntryDto(
                e.Id,
                e.OccurredAtUtc,
                e.ActorType.ToString(),
                e.ActorId,
                e.ActorName,
                e.Action,
                e.TargetType,
                e.TargetId,
                e.DetailsJson,
                e.IpAddress))
            .ToList();

        return new PagedResult<AuditEntryDto>(items, rows.Count > take ? items[^1].Id : null);
    }
}

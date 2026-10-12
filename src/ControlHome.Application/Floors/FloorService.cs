using System.Globalization;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Auditing;
using ControlHome.Application.Common;
using ControlHome.Domain.Files;
using ControlHome.Domain.Geometry;
using ControlHome.Domain.Homes;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Application.Floors;

/// <summary>
/// Floors and their rooms. Every change to a floor (properties or layout) carries the <see cref="FloorDto.RowVersion"/>
/// the client edited: an older one gets 409 <c>floors.concurrency</c> — checked up front and again by the database
/// (rowversion concurrency token), so of two admins saving the same floor at once exactly one wins.
/// </summary>
public sealed class FloorService(IControlHomeDb db, IAuditLog auditLog, TimeProvider timeProvider)
{
    public const string FloorTarget = "floor";

    private const double Cm2PerM2 = 10_000;

    public async Task<IReadOnlyList<FloorSummaryDto>> ListAsync(CancellationToken cancellationToken) =>
        await db.Floors
            .AsNoTracking()
            .OrderBy(f => f.Level)
            .ThenBy(f => f.Id)
            .Select(f => new FloorSummaryDto(f.Id, f.Name, f.Level, f.Rooms.Count))
            .ToListAsync(cancellationToken);

    public async Task<Result<FloorDto>> GetAsync(int id, CancellationToken cancellationToken)
    {
        var floor = await db.Floors.AsNoTracking().Include(f => f.Rooms).SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        return floor is null ? NotFound() : ToDto(floor);
    }

    /// <summary>All rooms of all floors, by floor level and then room order.</summary>
    public async Task<IReadOnlyList<RoomListItemDto>> ListRoomsAsync(CancellationToken cancellationToken) =>
        await (from room in db.Rooms.AsNoTracking()
               join floor in db.Floors.AsNoTracking() on room.FloorId equals floor.Id
               orderby floor.Level, floor.Id, room.SortOrder, room.Id
               select new RoomListItemDto(room.Id, room.Name, floor.Id, floor.Name))
            .ToListAsync(cancellationToken);

    public async Task<Result<FloorDto>> CreateAsync(CreateFloorRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = timeProvider.GetUtcNow();
        var floor = new Floor
        {
            Name = request.Name.Trim(),
            Level = request.Level,
            WidthCm = request.WidthCm ?? Floor.DefaultWidthCm,
            HeightCm = request.HeightCm ?? Floor.DefaultHeightCm,
            GridCm = Floor.DefaultGridCm,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.Floors.Add(floor);
        await db.SaveChangesAsync(cancellationToken);

        await auditLog.WriteAsync(
            new AuditEvent(AuditActions.FloorCreated, FloorTarget, IdOf(floor), new { floor.Name, floor.Level }),
            cancellationToken);
        return ToDto(floor);
    }

    /// <summary>Changes name, level, size, grid and background; audits <c>floors.updated</c> with the changed fields.</summary>
    public async Task<Result<FloorDto>> UpdateAsync(int id, UpdateFloorRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var floor = await db.Floors.Include(f => f.Rooms).SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (floor is null)
        {
            return NotFound();
        }

        if (!IsCurrent(floor, request.RowVersion, out var rowVersion))
        {
            return Concurrency();
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var points = floor.Rooms.SelectMany(r => r.Polygon).ToList();
        if (points.Any(p => p.X > request.WidthCm))
        {
            errors["widthCm"] = [ErrorCodes.GeometryOutOfBounds];
        }

        if (points.Any(p => p.Y > request.HeightCm))
        {
            errors["heightCm"] = [ErrorCodes.GeometryOutOfBounds];
        }

        var background = request.Background;
        if (background is not null && background.FileId != floor.Background?.FileId
            && !await db.StoredFiles.AnyAsync(
                f => f.Id == background.FileId && f.Purpose == FilePurposes.FloorBackground,
                cancellationToken))
        {
            errors["background.fileId"] = [ErrorCodes.FilesNotFound];
        }

        if (errors.Count > 0)
        {
            return AppError.FieldErrors(errors);
        }

        var changed = new List<string>();
        Set(floor.Name, request.Name.Trim(), v => floor.Name = v, "name");
        Set(floor.Level, request.Level, v => floor.Level = v, "level");
        Set(floor.WidthCm, request.WidthCm, v => floor.WidthCm = v, "widthCm");
        Set(floor.HeightCm, request.HeightCm, v => floor.HeightCm = v, "heightCm");
        Set(floor.GridCm, request.GridCm, v => floor.GridCm = v, "gridCm");
        Set(ToDto(floor.Background), background, v => floor.Background = ToEntity(v), "background");

        if (changed.Count == 0)
        {
            return ToDto(floor);
        }

        return await SaveAsync(
            floor,
            rowVersion,
            new AuditEvent(AuditActions.FloorUpdated, FloorTarget, IdOf(floor), new { Fields = changed }),
            cancellationToken);

        void Set<T>(T current, T value, Action<T> assign, string field)
        {
            if (!EqualityComparer<T>.Default.Equals(current, value))
            {
                assign(value);
                changed.Add(field);
            }
        }
    }

    /// <summary>
    /// Replaces the rooms (listed with an id: updated; without: created; not listed: deleted) in one save. Every outline
    /// must pass <see cref="RoomPolygonRules"/> for this floor's size; violations come back together as field errors
    /// (<c>rooms[i].polygon</c>, <c>rooms[i].id</c> for a room that is not on this floor).
    /// </summary>
    public async Task<Result<FloorDto>> SaveLayoutAsync(int id, SaveFloorLayoutRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var floor = await db.Floors.Include(f => f.Rooms).SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (floor is null)
        {
            return NotFound();
        }

        if (!IsCurrent(floor, request.RowVersion, out var rowVersion))
        {
            return Concurrency();
        }

        var existing = floor.Rooms.ToDictionary(r => r.Id);
        var polygons = new List<List<PointCm>>(request.Rooms.Count);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        for (var i = 0; i < request.Rooms.Count; i++)
        {
            var input = request.Rooms[i];
            if (input.Id is { } roomId && !existing.ContainsKey(roomId))
            {
                errors[Path(i, "id")] = [ErrorCodes.RoomsNotFound];
            }

            var polygon = input.Polygon.Select(p => new PointCm(p.X, p.Y)).ToList();
            if (RoomPolygonRules.Validate(polygon, floor.WidthCm, floor.HeightCm) is { } code)
            {
                errors[Path(i, "polygon")] = [code];
            }

            polygons.Add(polygon);
        }

        if (errors.Count > 0)
        {
            return AppError.FieldErrors(errors);
        }

        var kept = new HashSet<int>();
        var added = 0;
        for (var i = 0; i < request.Rooms.Count; i++)
        {
            var input = request.Rooms[i];
            Room room;
            if (input.Id is { } roomId)
            {
                room = existing[roomId];
                kept.Add(roomId);
            }
            else
            {
                room = new Room();
                floor.Rooms.Add(room);
                added++;
            }

            room.Name = input.Name.Trim();
            room.Icon = input.Icon;
            room.ColorToken = input.ColorToken;
            room.Polygon = polygons[i];
            room.SortOrder = i;
        }

        var removed = existing.Values.Where(r => !kept.Contains(r.Id)).ToList();
        foreach (var room in removed)
        {
            floor.Rooms.Remove(room);
            db.Rooms.Remove(room);
        }

        return await SaveAsync(
            floor,
            rowVersion,
            new AuditEvent(
                AuditActions.FloorLayoutSaved,
                FloorTarget,
                IdOf(floor),
                new { Rooms = request.Rooms.Count, Added = added, Removed = removed.Count }),
            cancellationToken);

        static string Path(int index, string field) => string.Create(CultureInfo.InvariantCulture, $"rooms[{index}].{field}");
    }

    /// <summary>Deletes the floor and its rooms.</summary>
    public async Task<Result> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        var floor = await db.Floors.SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (floor is null)
        {
            return NotFound();
        }

        db.Floors.Remove(floor); // rooms go with it (ON DELETE CASCADE)
        await auditLog.WriteAsync(
            new AuditEvent(AuditActions.FloorDeleted, FloorTarget, IdOf(floor), new { floor.Name }),
            cancellationToken);
        return Result.Success();
    }

    /// <summary>
    /// Saves the pending changes and the audit entry together, bumping the floor's rowversion (UpdatedAtUtc is always
    /// written) with the client's rowversion as the expected original value.
    /// </summary>
    private async Task<Result<FloorDto>> SaveAsync(Floor floor, byte[] rowVersion, AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        floor.UpdatedAtUtc = timeProvider.GetUtcNow();
        var entry = db.Floors.Entry(floor);
        entry.Property(f => f.UpdatedAtUtc).IsModified = true;
        entry.Property(f => f.RowVersion).OriginalValue = rowVersion;

        try
        {
            await auditLog.WriteAsync(auditEvent, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Concurrency();
        }

        return ToDto(floor);
    }

    private static bool IsCurrent(Floor floor, string clientRowVersion, out byte[] rowVersion) =>
        FloorRules.TryParseRowVersion(clientRowVersion, out rowVersion) && floor.RowVersion.AsSpan().SequenceEqual(rowVersion);

    private static string IdOf(Floor floor) => floor.Id.ToString(CultureInfo.InvariantCulture);

    private static AppError NotFound() => AppError.NotFound(ErrorCodes.FloorsNotFound, "No such floor.");

    private static AppError Concurrency() =>
        AppError.Conflict(ErrorCodes.FloorsConcurrency, "The floor was changed by someone else. Reload it and try again.");

    private static FloorDto ToDto(Floor floor) => new(
        floor.Id,
        floor.Name,
        floor.Level,
        floor.WidthCm,
        floor.HeightCm,
        floor.GridCm,
        ToDto(floor.Background),
        floor.Rooms.OrderBy(r => r.SortOrder).ThenBy(r => r.Id).Select(ToDto).ToList(),
        FloorRules.FormatRowVersion(floor.RowVersion));

    private static RoomDto ToDto(Room room) => new(
        room.Id,
        room.Name,
        room.Icon,
        room.ColorToken,
        room.Polygon.Select(p => new PointCmDto(p.X, p.Y)).ToList(),
        Math.Round(PolygonMath.AreaCm2(room.Polygon) / Cm2PerM2, 2, MidpointRounding.AwayFromZero),
        room.SortOrder);

    private static FloorBackgroundDto? ToDto(FloorBackground? background) =>
        background is null
            ? null
            : new FloorBackgroundDto(background.FileId, background.Opacity, background.OffsetXCm, background.OffsetYCm, background.CmPerPx);

    private static FloorBackground? ToEntity(FloorBackgroundDto? background) =>
        background is null
            ? null
            : new FloorBackground
            {
                FileId = background.FileId,
                Opacity = background.Opacity,
                OffsetXCm = background.OffsetXCm,
                OffsetYCm = background.OffsetYCm,
                CmPerPx = background.CmPerPx,
            };
}

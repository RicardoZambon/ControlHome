using System.Globalization;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Auditing;
using ControlHome.Application.Common;
using ControlHome.Application.Realtime;
using ControlHome.Domain.Geometry;
using ControlHome.Domain.Homes;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Application.Placements;

/// <summary>
/// Endpoints and devices on the floor plans. Placing (or moving) a target assigns its room: the room whose outline contains
/// the point (<see cref="RoomLocator"/>), or none when it is outside every room — an endpoint without a room of its own shows
/// in its device's room. Every change is audited and pushed to clients (hub <c>DevicesChanged</c>: rooms may have changed).
/// </summary>
public sealed class PlacementService(IControlHomeDb db, IRealtimePublisher publisher, IAuditLog auditLog)
{
    public const string PlacementTarget = "placement";

    /// <summary>All placements (or those of one floor), with the room each point is in.</summary>
    public async Task<IReadOnlyList<PlacementDto>> ListAsync(int? floorId, CancellationToken cancellationToken)
    {
        var floors = db.Floors.AsNoTracking().Include(f => f.Rooms).AsQueryable();
        var placements = db.Placements.AsNoTracking();
        if (floorId is { } id)
        {
            floors = floors.Where(f => f.Id == id);
            placements = placements.Where(p => p.FloorId == id);
        }

        var rooms = (await floors.ToListAsync(cancellationToken)).ToDictionary(f => f.Id, f => (IReadOnlyList<Room>)f.Rooms);
        return (await placements.OrderBy(p => p.FloorId).ThenBy(p => p.Id).ToListAsync(cancellationToken))
            .Select(p => ToDto(p, rooms.GetValueOrDefault(p.FloorId) ?? []))
            .ToList();
    }

    /// <summary>
    /// Places the endpoint or device on the floor (moving an existing placement, also from another floor) and assigns its
    /// room. Errors: 400 <c>validation.failed</c> with <c>errors.floorId = [floors.not_found]</c>,
    /// <c>errors.endpointId = [endpoints.not_found]</c>, <c>errors.deviceId = [devices.not_found]</c>, or
    /// <c>errors.xCm</c> / <c>errors.yCm</c> = <c>[geometry.out_of_bounds]</c> for a point off the floor.
    /// </summary>
    public async Task<Result<PlacementDto>> PlaceAsync(PlaceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var floor = await db.Floors.AsNoTracking().Include(f => f.Rooms).SingleOrDefaultAsync(f => f.Id == request.FloorId, cancellationToken);
        if (floor is null)
        {
            errors["floorId"] = [ErrorCodes.FloorsNotFound];
        }
        else
        {
            if (!(request.XCm >= 0 && request.XCm <= floor.WidthCm))
            {
                errors["xCm"] = [ErrorCodes.GeometryOutOfBounds];
            }

            if (!(request.YCm >= 0 && request.YCm <= floor.HeightCm))
            {
                errors["yCm"] = [ErrorCodes.GeometryOutOfBounds];
            }
        }

        Domain.Devices.Endpoint? endpoint = null;
        Domain.Devices.Device? device = null;
        if (request.EndpointId is { } endpointId)
        {
            endpoint = await db.Endpoints.SingleOrDefaultAsync(e => e.Id == endpointId, cancellationToken);
            if (endpoint is null)
            {
                errors["endpointId"] = [ErrorCodes.EndpointsNotFound];
            }
        }
        else if (request.DeviceId is { } deviceId)
        {
            device = await db.Devices.SingleOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
            if (device is null)
            {
                errors["deviceId"] = [ErrorCodes.DevicesNotFound];
            }
        }

        if (errors.Count > 0 || floor is null)
        {
            return AppError.FieldErrors(errors);
        }

        var placement = endpoint is not null
            ? await db.Placements.SingleOrDefaultAsync(p => p.EndpointId == endpoint.Id, cancellationToken)
            : await db.Placements.SingleOrDefaultAsync(p => p.DeviceId == device!.Id, cancellationToken);
        var created = placement is null;
        if (placement is null)
        {
            placement = new Placement { EndpointId = endpoint?.Id, DeviceId = device?.Id };
            db.Placements.Add(placement);
        }

        placement.FloorId = floor.Id;
        placement.XCm = request.XCm;
        placement.YCm = request.YCm;

        var roomId = RoomLocator.Find(floor.Rooms, placement.XCm, placement.YCm)?.Id;
        if (endpoint is not null)
        {
            endpoint.RoomId = roomId;
        }
        else
        {
            device!.RoomId = roomId;
        }

        await db.SaveChangesAsync(cancellationToken);
        await auditLog.WriteAsync(
            new AuditEvent(
                AuditActions.PlacementSaved,
                PlacementTarget,
                IdOf(placement.Id),
                new { placement.FloorId, placement.EndpointId, placement.DeviceId, RoomId = roomId, Created = created }),
            cancellationToken);
        await publisher.DevicesChangedAsync(cancellationToken);
        return ToDto(placement, floor.Rooms);
    }

    /// <summary>Takes the endpoint or device off the plan (its room stays); 404 <c>placements.not_found</c>.</summary>
    public async Task<Result> DeleteAsync(int id, CancellationToken cancellationToken)
    {
        var placement = await db.Placements.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (placement is null)
        {
            return AppError.NotFound(ErrorCodes.PlacementsNotFound, "No such placement.");
        }

        db.Placements.Remove(placement);
        await auditLog.WriteAsync(
            new AuditEvent(AuditActions.PlacementDeleted, PlacementTarget, IdOf(id), new { placement.FloorId, placement.EndpointId, placement.DeviceId }),
            cancellationToken);
        await publisher.DevicesChangedAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>The DTO of a placement on a floor with <paramref name="rooms"/>.</summary>
    public static PlacementDto ToDto(Placement placement, IReadOnlyList<Room> rooms)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return new PlacementDto(
            placement.Id,
            placement.FloorId,
            placement.EndpointId,
            placement.DeviceId,
            placement.XCm,
            placement.YCm,
            RoomLocator.Find(rooms, placement.XCm, placement.YCm)?.Id);
    }

    private static string IdOf(int id) => id.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Which room a point of a floor plan is in.</summary>
public static class RoomLocator
{
    /// <summary>
    /// The first room (by display order, then id) whose outline contains the point — its edges included — or <c>null</c>
    /// when the point is outside every room.
    /// </summary>
    public static Room? Find(IEnumerable<Room> rooms, double xCm, double yCm)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        var point = new PointCm(xCm, yCm);
        return rooms.OrderBy(r => r.SortOrder).ThenBy(r => r.Id).FirstOrDefault(r => PolygonMath.Contains(r.Polygon, point));
    }
}

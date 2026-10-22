using System.Globalization;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Auditing;
using ControlHome.Application.Common;
using ControlHome.Application.Realtime;
using ControlHome.Application.Runtime;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Application.Devices;

/// <summary>
/// The user fields of devices and endpoints (name, icon, room, hidden, favorite). Connectors never change them; every change
/// is audited and pushed to clients (hub <c>DevicesChanged</c>).
/// </summary>
public sealed class DeviceAdminService(
    IControlHomeDb db,
    DeviceQueryService queries,
    IDeviceInventoryAdmin inventory,
    IRuntimeStatus runtimeStatus,
    IRealtimePublisher publisher,
    IAuditLog auditLog)
{
    public const string DeviceTarget = "device";
    public const string EndpointTarget = "endpoint";

    /// <summary>
    /// The new device takes over the old one (see <see cref="IDeviceInventoryAdmin.ReplaceAsync"/>); returns it. Errors:
    /// 404 <c>devices.not_found</c>; 400 <c>validation.failed</c> with <c>errors.newDeviceId</c> = <c>devices.not_found</c>
    /// or <c>devices.replace_same</c>; 409 <c>runtime.standby</c> and 503 <c>runtime.starting</c> like integration changes
    /// (the process that runs the devices keeps them in memory: only it may delete one and move its attributes).
    /// </summary>
    public async Task<Result<DeviceDetailDto>> ReplaceAsync(Guid id, ReplaceDeviceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (runtimeStatus.IntegrationChangeRefusal() is { } refused)
        {
            return refused;
        }

        var replaced = await inventory.ReplaceAsync(id, request.NewDeviceId, cancellationToken);
        if (replaced.IsFailure)
        {
            return replaced.Error;
        }

        await auditLog.WriteAsync(
            new AuditEvent(AuditActions.DeviceReplaced, DeviceTarget, IdOf(id), new { NewDeviceId = request.NewDeviceId }),
            cancellationToken);
        await publisher.DevicesChangedAsync(cancellationToken);
        return await queries.GetAsync(request.NewDeviceId, cancellationToken);
    }

    /// <summary>Errors: 404 <c>devices.not_found</c>; 400 <c>validation.failed</c> with <c>errors.roomId = [rooms.not_found]</c>.</summary>
    public async Task<Result<DeviceDetailDto>> UpdateDeviceAsync(Guid id, UpdateDeviceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var device = await db.Devices.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (device is null)
        {
            return AppError.NotFound(ErrorCodes.DevicesNotFound, "No such device.");
        }

        if (!await RoomExistsAsync(request.RoomId, cancellationToken))
        {
            return RoomNotFound();
        }

        var name = request.Name.Trim();
        var fields = new List<string>();
        if (!string.Equals(device.Name, name, StringComparison.Ordinal))
        {
            device.Name = name;
            fields.Add("name");
        }

        if (!string.Equals(device.Icon, request.Icon, StringComparison.Ordinal))
        {
            device.Icon = request.Icon;
            fields.Add("icon");
        }

        if (device.RoomId != request.RoomId)
        {
            device.RoomId = request.RoomId;
            fields.Add("roomId");
        }

        if (fields.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            await auditLog.WriteAsync(new AuditEvent(AuditActions.DeviceUpdated, DeviceTarget, IdOf(id), new { Fields = fields }), cancellationToken);
            await publisher.DevicesChangedAsync(cancellationToken);
        }

        return await queries.GetAsync(id, cancellationToken);
    }

    /// <summary>Errors: 404 <c>endpoints.not_found</c>; 400 <c>validation.failed</c> with <c>errors.roomId = [rooms.not_found]</c>.</summary>
    public async Task<Result<EndpointDto>> UpdateEndpointAsync(Guid id, UpdateEndpointRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var endpoint = await db.Endpoints.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (endpoint is null)
        {
            return EndpointNotFound();
        }

        if (!await RoomExistsAsync(request.RoomId, cancellationToken))
        {
            return RoomNotFound();
        }

        var name = request.Name?.Trim();
        var fields = new List<string>();
        if (!string.Equals(endpoint.Name, name, StringComparison.Ordinal))
        {
            endpoint.Name = name;
            fields.Add("name");
        }

        if (!string.Equals(endpoint.Icon, request.Icon, StringComparison.Ordinal))
        {
            endpoint.Icon = request.Icon;
            fields.Add("icon");
        }

        if (endpoint.RoomId != request.RoomId)
        {
            endpoint.RoomId = request.RoomId;
            fields.Add("roomId");
        }

        if (endpoint.Hidden != request.Hidden)
        {
            endpoint.Hidden = request.Hidden;
            fields.Add("hidden");
        }

        if (fields.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            await auditLog.WriteAsync(new AuditEvent(AuditActions.EndpointUpdated, EndpointTarget, IdOf(id), new { Fields = fields }), cancellationToken);
            await publisher.DevicesChangedAsync(cancellationToken);
        }

        return await EndpointDtoAsync(endpoint.DeviceId, id, cancellationToken);
    }

    /// <summary>Errors: 404 <c>endpoints.not_found</c>.</summary>
    public async Task<Result<EndpointDto>> SetFavoriteAsync(Guid id, SetFavoriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var endpoint = await db.Endpoints.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (endpoint is null)
        {
            return EndpointNotFound();
        }

        if (endpoint.Favorite != request.Favorite)
        {
            endpoint.Favorite = request.Favorite;
            await db.SaveChangesAsync(cancellationToken);
            await auditLog.WriteAsync(
                new AuditEvent(AuditActions.EndpointFavoriteChanged, EndpointTarget, IdOf(id), new { request.Favorite }),
                cancellationToken);
            await publisher.DevicesChangedAsync(cancellationToken);
        }

        return await EndpointDtoAsync(endpoint.DeviceId, id, cancellationToken);
    }

    private async Task<Result<EndpointDto>> EndpointDtoAsync(Guid deviceId, Guid endpointId, CancellationToken cancellationToken)
    {
        var device = await queries.GetAsync(deviceId, cancellationToken);
        if (device.IsFailure)
        {
            return device.Error;
        }

        var endpoint = device.Value.Endpoints.SingleOrDefault(e => e.Id == endpointId);
        if (endpoint is null)
        {
            return EndpointNotFound();
        }

        return endpoint;
    }

    private async Task<bool> RoomExistsAsync(int? roomId, CancellationToken cancellationToken) =>
        roomId is not { } id || await db.Rooms.AnyAsync(r => r.Id == id, cancellationToken);

    private static AppError RoomNotFound() =>
        AppError.FieldErrors(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["roomId"] = [ErrorCodes.RoomsNotFound] });

    private static AppError EndpointNotFound() => AppError.NotFound(ErrorCodes.EndpointsNotFound, "No such device control.");

    private static string IdOf(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);
}

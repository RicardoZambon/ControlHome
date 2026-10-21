using ControlHome.Application.Abstractions;
using ControlHome.Application.Common;
using ControlHome.Domain.Capabilities;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Application.Devices;

/// <summary>A camera still, ready to send.</summary>
/// <param name="ContentType"><c>image/jpeg</c>, <c>image/png</c> or <c>image/webp</c>.</param>
public sealed record CameraImage(string ContentType, byte[] Data);

/// <summary>
/// Camera stills behind the app's own authorization: the connector's runtime hands out the image; clients never see a
/// camera URL or its credentials. Only image types a browser renders as an image are passed on.
/// </summary>
public sealed class CameraService(IControlHomeDb db, ICommandDispatcher dispatcher, ICameraSnapshotProvider snapshots)
{
    private static readonly HashSet<string> _imageTypes = new(["image/jpeg", "image/png", "image/webp"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The latest still of the camera at the endpoint. Errors: 404 <c>endpoints.not_found</c>; 404 <c>cameras.no_snapshot</c>
    /// (not a camera, or no image right now); 503 <c>commands.unavailable</c> (its integration is not running).
    /// </summary>
    public async Task<Result<CameraImage>> GetSnapshotAsync(Guid endpointId, CancellationToken cancellationToken)
    {
        var camera = await (from endpoint in db.Endpoints.AsNoTracking()
                            join device in db.Devices.AsNoTracking() on endpoint.DeviceId equals device.Id
                            where endpoint.Id == endpointId
                            select new
                            {
                                endpoint.Key,
                                device.IntegrationId,
                                device.ExternalId,
                                IsCamera = endpoint.Capabilities.Any(c => c.CapabilityId == CapabilityIds.Camera && !c.Retired),
                            })
            .SingleOrDefaultAsync(cancellationToken);
        if (camera is null)
        {
            return AppError.NotFound(ErrorCodes.EndpointsNotFound, "No such device control.");
        }

        if (!camera.IsCamera)
        {
            return NoSnapshot();
        }

        if (!dispatcher.IsRunning(camera.IntegrationId))
        {
            return AppError.Unavailable(ErrorCodes.CommandsUnavailable, "The camera's integration is not running.");
        }

        var snapshot = await snapshots.GetSnapshotAsync(camera.IntegrationId, camera.ExternalId, camera.Key, cancellationToken);
        return snapshot is null || !_imageTypes.Contains(snapshot.ContentType) || snapshot.Data.IsEmpty
            ? NoSnapshot()
            : new CameraImage(snapshot.ContentType.ToLowerInvariant(), snapshot.Data.ToArray());
    }

    private static AppError NoSnapshot() => AppError.NotFound(ErrorCodes.CamerasNoSnapshot, "The camera has no picture right now.");
}

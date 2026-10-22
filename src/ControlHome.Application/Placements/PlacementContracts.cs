using FluentValidation;

namespace ControlHome.Application.Placements;

/// <summary>
/// Places an endpoint or a whole device on a floor, or moves it there (<c>PUT /api/placements</c>, Admin). Exactly one of
/// <paramref name="EndpointId"/> and <paramref name="DeviceId"/>. The target's room becomes the room containing the point
/// (none when it is outside every room).
/// </summary>
/// <param name="EndpointId">The endpoint to place (one control of a multi-channel device, or any single-endpoint device).</param>
/// <param name="DeviceId">The device to place as a whole.</param>
/// <param name="XCm">0 … the floor's width.</param>
/// <param name="YCm">0 … the floor's height.</param>
public sealed record PlaceRequest(Guid? EndpointId, Guid? DeviceId, int FloorId, double XCm, double YCm);

/// <summary>An endpoint or device on a floor plan.</summary>
/// <param name="EndpointId">Set when an endpoint is placed (then <paramref name="DeviceId"/> is <c>null</c>).</param>
/// <param name="DeviceId">Set when a whole device is placed (then <paramref name="EndpointId"/> is <c>null</c>).</param>
/// <param name="RoomId">The room that contains the point (first in the floor's room order), or <c>null</c>.</param>
public sealed record PlacementDto(int Id, int FloorId, Guid? EndpointId, Guid? DeviceId, double XCm, double YCm, int? RoomId);

public sealed class PlaceRequestValidator : AbstractValidator<PlaceRequest>
{
    public const string TargetMessage = "Give exactly one of endpointId and deviceId.";

    public PlaceRequestValidator()
    {
        RuleFor(x => x.EndpointId)
            .Must((request, endpointId) => (endpointId is null) != (request.DeviceId is null))
            .WithMessage(TargetMessage);
        RuleFor(x => x.EndpointId).NotEqual(Guid.Empty).When(x => x.EndpointId is not null);
        RuleFor(x => x.DeviceId).NotEqual(Guid.Empty).When(x => x.DeviceId is not null);
        RuleFor(x => x.FloorId).GreaterThan(0);
        RuleFor(x => x.XCm).Must(double.IsFinite).GreaterThanOrEqualTo(0);
        RuleFor(x => x.YCm).Must(double.IsFinite).GreaterThanOrEqualTo(0);
    }
}

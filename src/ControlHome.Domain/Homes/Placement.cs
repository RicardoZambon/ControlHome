namespace ControlHome.Domain.Homes;

/// <summary>
/// Where an endpoint (one control of a device) or a whole device sits on a <see cref="Floor"/>, in the floor's centimetre
/// coordinates. Exactly one of <see cref="EndpointId"/> and <see cref="DeviceId"/> is set; each endpoint and each device is
/// placed at most once. Placing assigns the target's room by a point-in-polygon test; deleting the target removes its
/// placement, while a floor with placements cannot be deleted.
/// </summary>
public sealed class Placement
{
    public int Id { get; set; }

    public int FloorId { get; set; }

    /// <summary>The placed endpoint, or <c>null</c> when a whole device is placed.</summary>
    public Guid? EndpointId { get; set; }

    /// <summary>The placed device, or <c>null</c> when an endpoint is placed.</summary>
    public Guid? DeviceId { get; set; }

    /// <summary>Horizontal position from the floor's left edge (0 … <see cref="Floor.WidthCm"/>).</summary>
    public double XCm { get; set; }

    /// <summary>Vertical position from the floor's top edge (0 … <see cref="Floor.HeightCm"/>).</summary>
    public double YCm { get; set; }
}

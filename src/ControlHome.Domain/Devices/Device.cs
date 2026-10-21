namespace ControlHome.Domain.Devices;

/// <summary>
/// A physical (or simulated) device of an <see cref="Integration"/>, identified by the connector's stable
/// <see cref="ExternalId"/> (unique per integration, never an IP address). Vendor fields (<see cref="Manufacturer"/>,
/// <see cref="Model"/>, <see cref="Firmware"/>, <see cref="DeviceClass"/>, <see cref="ConnectionsJson"/>) are updated by
/// inventory syncs; user fields (<see cref="Name"/> after import, <see cref="RoomId"/>, <see cref="Icon"/>) never are.
/// </summary>
public sealed class Device
{
    public Guid Id { get; set; }

    public Guid IntegrationId { get; set; }

    /// <summary>The connector's stable id for the device (serial, vendor device id, Zigbee IEEE address, …).</summary>
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>The device this one is reached through (an NVR for its cameras, a hub for its sensors).</summary>
    public Guid? ParentDeviceId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>A <see cref="DeviceClasses"/> value (e.g. <c>gate</c>); drives icons and the safety policy.</summary>
    public string? DeviceClass { get; set; }

    public string? Manufacturer { get; set; }

    public string? Model { get; set; }

    public string? Firmware { get; set; }

    /// <summary>
    /// Hardware identifiers as a JSON object of strings (<c>json</c> column), e.g. <c>{"mac":"…","serial":"…"}</c>; used to
    /// flag likely duplicates across integrations.
    /// </summary>
    public string ConnectionsJson { get; set; } = "{}";

    public Availability Availability { get; set; } = Availability.Unknown;

    public DateTimeOffset? LastSeenAtUtc { get; set; }

    /// <summary>The connector no longer reports the device. It keeps its name, room, placement and history; never auto-deleted.</summary>
    public bool IsOrphaned { get; set; }

    /// <summary>The room the device is in (its endpoints inherit it unless they set their own). Cleared when the room is deleted.</summary>
    public int? RoomId { get; set; }

    /// <summary>Icon key overriding the device class's default icon.</summary>
    public string? Icon { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public List<Endpoint> Endpoints { get; set; } = [];
}

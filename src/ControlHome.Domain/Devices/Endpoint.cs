namespace ControlHome.Domain.Devices;

/// <summary>
/// A controllable part of a <see cref="Device"/> (a channel of a 3-gang switch, a fridge compartment; most devices have one,
/// key <c>main</c>). Users place, name, favorite and hide endpoints; commands address an endpoint.
/// </summary>
/// <remarks>The name clashes with <c>Microsoft.AspNetCore.Http.Endpoint</c>: alias it in web-layer code.</remarks>
public sealed class Endpoint
{
    /// <summary>The key of a device's only (or primary) endpoint.</summary>
    public const string MainKey = "main";

    public Guid Id { get; set; }

    public Guid DeviceId { get; set; }

    /// <summary>The connector's key for the endpoint, unique within the device (e.g. <c>main</c>, <c>l1</c>, <c>switch_2</c>).</summary>
    public string Key { get; set; } = MainKey;

    /// <summary>User name; <c>null</c> shows the device name (plus the key when the device has several endpoints).</summary>
    public string? Name { get; set; }

    /// <summary>Overrides the device's room; <c>null</c> inherits it. Cleared when the room is deleted.</summary>
    public int? RoomId { get; set; }

    public string? Icon { get; set; }

    /// <summary>Hidden endpoints are left out of dashboards and pickers (still controllable from the device page).</summary>
    public bool Hidden { get; set; }

    /// <summary>Shown on the dashboard's favorites grid (household-wide).</summary>
    public bool Favorite { get; set; }

    /// <summary>Position among the device's endpoints (0-based).</summary>
    public int SortOrder { get; set; }

    public List<EndpointCapability> Capabilities { get; set; } = [];
}

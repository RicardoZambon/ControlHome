using ControlHome.Domain.Capabilities;

namespace ControlHome.Application.Realtime;

/// <summary>
/// The live-update hub (<c>/hubs/home</c>, SignalR, signed-in users only). It is server → client only: clients never call
/// hub methods. Each message has at most one argument; the names below are the client-side method names
/// (<c>connection.on(name, handler)</c>). After a (re)connect, clients resync with <c>GET /api/state</c> and
/// <c>GET /api/devices</c>.
/// </summary>
public static class HomeHubMessages
{
    /// <summary>The hub's path.</summary>
    public const string Path = "/hubs/home";

    /// <summary>Argument: <see cref="StateDelta"/>[] (batched, at most one per attribute, sent at most every ~50 ms).</summary>
    public const string StateChanged = "StateChanged";

    /// <summary>Argument: <see cref="AvailabilityChangedDto"/>.</summary>
    public const string AvailabilityChanged = "AvailabilityChanged";

    /// <summary>No argument: the device inventory changed (devices or endpoints added, renamed, moved): refetch the devices.</summary>
    public const string DevicesChanged = "DevicesChanged";

    /// <summary>Argument: <see cref="IntegrationStatusChangedDto"/>.</summary>
    public const string IntegrationStatusChanged = "IntegrationStatusChanged";

    /// <summary>Argument: <see cref="CommandStatusDto"/>.</summary>
    public const string CommandStatus = "CommandStatus";

    /// <summary>Argument: <see cref="Runtime.SystemStatusDto"/>.</summary>
    public const string SystemStatusChanged = "SystemStatusChanged";
}

/// <summary>A new current value of one attribute (hub <c>StateChanged</c>).</summary>
/// <param name="AttributeId">The attribute (see <c>AttributeDto.AttributeId</c>).</param>
/// <param name="Value">The value now; a delta always means the value is live (no longer <c>restored</c>).</param>
/// <param name="ObservedAtUtc">When the device observed it.</param>
public sealed record StateDelta(int AttributeId, StateValue Value, DateTimeOffset ObservedAtUtc);

/// <summary>A device became reachable or not (hub <c>AvailabilityChanged</c>).</summary>
/// <param name="Availability"><c>Unknown</c>, <c>Online</c> or <c>Offline</c>.</param>
public sealed record AvailabilityChangedDto(Guid DeviceId, string Availability);

/// <summary>An integration's status changed (hub <c>IntegrationStatusChanged</c>).</summary>
/// <param name="Id">The integration.</param>
/// <param name="Status">An <c>IntegrationDto.Status</c> value.</param>
/// <param name="Message">A short non-secret explanation, if any.</param>
public sealed record IntegrationStatusChangedDto(Guid Id, string Status, string? Message);

/// <summary>Progress of a command sent with <c>POST /api/endpoints/{id}/commands</c> (hub <c>CommandStatus</c>).</summary>
/// <param name="CommandId">The id from the command's 202 response.</param>
/// <param name="Status">
/// <c>pending</c> (accepted, not done yet), then one final status: <c>confirmed</c>, <c>failed</c>, <c>timed_out</c> or
/// <c>superseded</c> (a later command for the same control replaced it before it was sent).
/// </param>
/// <param name="Code">For <c>failed</c> and <c>timed_out</c>: an error code (<c>commands.*</c>), translated as <c>errors.&lt;code&gt;</c>.</param>
public sealed record CommandStatusDto(Guid CommandId, string Status, string? Code);

/// <summary>The values of <see cref="CommandStatusDto.Status"/>.</summary>
public static class CommandStatuses
{
    public const string Pending = "pending";
    public const string Confirmed = "confirmed";
    public const string Failed = "failed";
    public const string TimedOut = "timed_out";
    public const string Superseded = "superseded";
}

using System.Text.Json;
using ControlHome.Connectors.DahuaHttp.Protocol;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;

namespace ControlHome.Connectors.DahuaHttp.Model;

/// <summary>What one event means for the inventory: a state, a momentary event or a device's availability.</summary>
internal abstract record DahuaEffect(string DeviceId, string EndpointKey);

/// <summary>
/// A state to report. With <see cref="Pulse"/> the value is <c>true</c> for a moment: it falls back to <c>false</c> after
/// the runtime's hold time unless another pulse extends it (line crossing, instantaneous events).
/// </summary>
internal sealed record DahuaStateEffect(string DeviceId, string EndpointKey, string CapabilityId, string AttributeKey, StateValue Value, bool Pulse = false)
    : DahuaEffect(DeviceId, EndpointKey);

/// <summary>A momentary event to report (an access granted or denied at the door).</summary>
internal sealed record DahuaMomentaryEffect(string DeviceId, string EndpointKey, string CapabilityId, string AttributeKey, string Event, JsonElement? Data)
    : DahuaEffect(DeviceId, EndpointKey);

/// <summary>A device's availability (a recorder channel that lost its camera's video).</summary>
internal sealed record DahuaAvailabilityEffect(string DeviceId, Availability Availability, string? Reason)
    : DahuaEffect(DeviceId, DahuaInventory.MainEndpoint);

/// <summary>
/// The local event filter: the attach stream subscribes to <c>[All]</c> (firmware truncates or rejects long code lists),
/// and only these codes mean something here — every other code (and an index no device has) yields nothing.
/// <list type="table">
/// <item><term><c>VideoMotion</c></term><description>the camera's <c>motion</c> (Start → true, Stop → false, Pulse → a moment).</description></item>
/// <item><term><c>CrossLineDetection</c></term><description><c>motion</c> of the camera's <c>tripwire</c> endpoint (Start/Pulse → a moment, Stop → false).</description></item>
/// <item><term><c>CrossRegionDetection</c></term><description><c>motion</c> of its <c>intrusion</c> endpoint (Start/Stop, Pulse → a moment).</description></item>
/// <item><term><c>AlarmLocal</c></term><description><c>contact.open</c> of alarm input <c>alarm&lt;index+1&gt;</c> on the root device.</description></item>
/// <item><term><c>VideoLoss</c></term><description>a recorder channel's availability (Start → offline, Stop → online).</description></item>
/// <item><term><c>AccessControl</c></term><description>the door's momentary <c>access</c> event (<c>granted</c> when <c>Status</c> is 1).</description></item>
/// <item><term><c>DoorStatus</c></term><description>the door's <c>contact.open</c> (<c>Status</c> Open / Close).</description></item>
/// </list>
/// </summary>
internal static class DahuaEventMapper
{
    /// <summary>The access-event members kept (no card numbers, names or picture paths).</summary>
    private static readonly string[] _accessDataMembers = ["Status", "Method", "Type", "UserID", "ErrorCode", "Door"];

    public static IReadOnlyList<DahuaEffect> Map(DahuaEvent e, DahuaInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(inventory);
        return e.Code switch
        {
            "VideoMotion" => Motion(e, inventory, DahuaInventory.MainEndpoint, startIsPulse: false),
            "CrossLineDetection" => Motion(e, inventory, DahuaInventory.TripwireEndpoint, startIsPulse: true),
            "CrossRegionDetection" => Motion(e, inventory, DahuaInventory.IntrusionEndpoint, startIsPulse: false),
            "AlarmLocal" => AlarmInput(e, inventory),
            "VideoLoss" => VideoLoss(e, inventory),
            "AccessControl" => Access(e, inventory),
            "DoorStatus" => DoorStatus(e, inventory),
            _ => [],
        };
    }

    /// <summary>The codes <see cref="Map"/> understands.</summary>
    public static IReadOnlySet<string> KnownCodes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "VideoMotion", "CrossLineDetection", "CrossRegionDetection", "AlarmLocal", "VideoLoss", "AccessControl", "DoorStatus",
    };

    private static DahuaEffect[] Motion(DahuaEvent e, DahuaInventory inventory, string endpoint, bool startIsPulse)
    {
        if (inventory.Kind is not (DahuaDeviceKind.Camera or DahuaDeviceKind.Recorder) || inventory.CameraForIndex(e.Index) is not { } camera)
        {
            return [];
        }

        if (e.IsStop)
        {
            return [new DahuaStateEffect(camera, endpoint, CapabilityIds.Motion, "motion", StateValue.False)];
        }

        if (e.IsPulse || (e.IsStart && startIsPulse))
        {
            return [new DahuaStateEffect(camera, endpoint, CapabilityIds.Motion, "motion", StateValue.True, Pulse: true)];
        }

        return e.IsStart ? [new DahuaStateEffect(camera, endpoint, CapabilityIds.Motion, "motion", StateValue.True)] : [];
    }

    private static DahuaEffect[] AlarmInput(DahuaEvent e, DahuaInventory inventory)
    {
        var input = (e.Index ?? 0) + 1;
        if (input > DahuaInventory.MaxAlarmInputs || inventory.Kind == DahuaDeviceKind.XpeIntercom || !(e.IsStart || e.IsStop))
        {
            return [];
        }

        return [new DahuaStateEffect(inventory.RootId, DahuaInventory.AlarmEndpointKey(input), CapabilityIds.Contact, "open", StateValue.Bool(e.IsStart))];
    }

    private static DahuaEffect[] VideoLoss(DahuaEvent e, DahuaInventory inventory)
    {
        if (inventory.Kind != DahuaDeviceKind.Recorder || inventory.CameraForIndex(e.Index) is not { } camera || !(e.IsStart || e.IsStop))
        {
            return [];
        }

        return [e.IsStart
            ? new DahuaAvailabilityEffect(camera, Availability.Offline, "The recorder has no video from this channel.")
            : new DahuaAvailabilityEffect(camera, Availability.Online, null)];
    }

    private static DahuaEffect[] Access(DahuaEvent e, DahuaInventory inventory)
    {
        if (inventory.Kind != DahuaDeviceKind.AccessControl || (e.Index ?? 0) != 0)
        {
            return [];
        }

        var status = e.DataInteger("Status");
        var errorCode = e.DataInteger("ErrorCode");
        var outcome = status switch
        {
            1 => "granted",
            0 => "denied",
            _ when errorCode is 0 => "granted",
            _ when errorCode is not null => "denied",
            _ => "unknown",
        };
        return [new DahuaMomentaryEffect(inventory.RootId, DahuaInventory.MainEndpoint, CapabilityIds.Generic, DahuaInventory.AccessAttribute, outcome, AccessData(e))];
    }

    private static DahuaEffect[] DoorStatus(DahuaEvent e, DahuaInventory inventory)
    {
        if (inventory.Kind != DahuaDeviceKind.AccessControl || (e.Index ?? 0) != 0)
        {
            return [];
        }

        return e.DataText("Status") switch
        {
            { } open when open.Equals("Open", StringComparison.OrdinalIgnoreCase) =>
                [new DahuaStateEffect(inventory.RootId, DahuaInventory.MainEndpoint, CapabilityIds.Contact, "open", StateValue.True)],
            { } closed when closed.Equals("Close", StringComparison.OrdinalIgnoreCase) || closed.Equals("Closed", StringComparison.OrdinalIgnoreCase) =>
                [new DahuaStateEffect(inventory.RootId, DahuaInventory.MainEndpoint, CapabilityIds.Contact, "open", StateValue.False)],
            _ => [],
        };
    }

    /// <summary>The allowlisted members of an access event (camelCase), or <c>null</c> when it carries none of them.</summary>
    private static JsonElement? AccessData(DahuaEvent e)
    {
        var kept = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var member in _accessDataMembers)
        {
            if (e.DataMember(member) is { ValueKind: JsonValueKind.String or JsonValueKind.Number } value)
            {
                var key = member == "UserID" ? "userId" : char.ToLowerInvariant(member[0]) + member[1..];
                kept[key] = value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : value.ToString();
            }
        }

        return kept.Count == 0 ? null : JsonSerializer.SerializeToElement(kept);
    }
}

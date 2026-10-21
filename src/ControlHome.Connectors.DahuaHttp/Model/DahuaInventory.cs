using System.Globalization;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;

namespace ControlHome.Connectors.DahuaHttp.Model;

/// <summary>What the inventory is built from (the device's answers during startup).</summary>
/// <param name="FallbackId">The root device's external id when the firmware reports no serial number.</param>
/// <param name="Channels">The recorder's video channels (1-based number, title); ignored for other kinds.</param>
/// <param name="AlarmInputs">The number of alarm inputs (<c>alarm.cgi?action=getInSlots</c>); 0 when unknown.</param>
/// <param name="SmartRules">The enabled IVS rule types per 1-based video channel (<c>VideoAnalyseRule</c>).</param>
/// <param name="Language"><c>pt-BR</c> or <c>en</c>: the language of the names the devices are first imported with.</param>
internal sealed record DahuaInventoryInput(
    DahuaDeviceKind Kind,
    DahuaIdentity Identity,
    string FallbackId,
    IReadOnlyList<(int Number, string? Title)> Channels,
    int AlarmInputs,
    IReadOnlyDictionary<int, IReadOnlySet<string>> SmartRules,
    string Language);

/// <summary>
/// The devices of one Dahua-family integration (one physical device) and how the protocol's channel indexes map onto
/// them. Endpoints for smart rules and alarm inputs the device did not announce at startup are added when their first
/// event arrives (<see cref="EnsureEndpoint"/>).
/// <list type="bullet">
/// <item>Camera: one device (<c>camera</c>), endpoint <c>main</c> = <c>camera</c> + <c>motion</c>.</item>
/// <item>Recorder: a parent (<c>nvr</c>, <c>generic</c> channel count) and one camera child per imported channel
/// (<c>&lt;serial&gt;:ch&lt;n&gt;</c>).</item>
/// <item>Access terminal: one device (<c>door</c>), endpoint <c>main</c> = <c>doorOpener</c> + <c>contact</c> (door sensor)
/// + <c>generic</c> momentary <c>access</c> (<c>granted</c> / <c>denied</c> / <c>unknown</c>).</item>
/// <item>XPE door station: one device (<c>intercom</c>) with <c>doorOpener</c>.</item>
/// <item>Smart rules: endpoints <c>tripwire</c> (line crossing) and <c>intrusion</c> (area) with <c>motion</c> on the camera;
/// alarm inputs: endpoints <c>alarm&lt;n&gt;</c> with <c>contact</c> on the root device.</item>
/// </list>
/// </summary>
internal sealed class DahuaInventory
{
    public const string MainEndpoint = "main";
    public const string TripwireEndpoint = "tripwire";
    public const string IntrusionEndpoint = "intrusion";
    public const string AccessAttribute = "access";
    public const string ChannelsAttribute = "channels";
    public const int MaxAlarmInputs = 64;

    public const string LineCrossingRule = "CrossLineDetection";
    public const string IntrusionRule = "CrossRegionDetection";

    /// <summary>The access events (the <c>generic</c> momentary attribute of an access terminal's door).</summary>
    public static readonly IReadOnlyList<string> AccessEvents = ["granted", "denied", "unknown"];

    private static readonly CapabilityDefinition _recorderGeneric = new(
        CapabilityIds.Generic,
        1,
        [
            new AttributeDefinition(
                ChannelsAttribute,
                new NumberSchema(0, ChannelSelection.MaxChannel, 1, null, Integer: true),
                null,
                StateClass.None,
                AttributeCategory.Diagnostic,
                Recorded: false,
                Writable: false),
        ],
        []);

    private static readonly CapabilityDefinition _accessGeneric = new(
        CapabilityIds.Generic,
        1,
        [
            new AttributeDefinition(
                AccessAttribute,
                new MomentarySchema { Events = AccessEvents },
                null,
                StateClass.None,
                AttributeCategory.Primary,
                Recorded: true,
                Writable: false),
        ],
        []);

    private readonly Lock _lock = new();
    private readonly List<DeviceModel> _devices = [];
    private readonly Dictionary<string, DeviceModel> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<int, DeviceModel> _byChannel = [];
    private readonly string _language;

    private DahuaInventory(DahuaDeviceKind kind, string rootId, string language)
    {
        Kind = kind;
        RootId = rootId;
        _language = language;
    }

    public DahuaDeviceKind Kind { get; }

    /// <summary>The external id of the physical device (its serial number, else the fallback id).</summary>
    public string RootId { get; }

    /// <summary>The number of recorder channels imported (0 for other kinds).</summary>
    public int ImportedChannels { get; private set; }

    /// <summary>Every device, parents first, as the connector reports them now.</summary>
    public IReadOnlyList<DiscoveredDevice> Devices
    {
        get
        {
            lock (_lock)
            {
                return [.. _devices.Select(d => d.Describe())];
            }
        }
    }

    /// <summary>The external ids of the camera devices (each with its 1-based video channel).</summary>
    public IReadOnlyList<(string DeviceId, int Channel)> Cameras
    {
        get
        {
            lock (_lock)
            {
                return [.. _devices.Where(d => d.Channel is not null).Select(d => (d.Id, d.Channel!.Value))];
            }
        }
    }

    public static DahuaInventory Create(DahuaInventoryInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var identity = input.Identity;
        var serial = string.IsNullOrWhiteSpace(identity.SerialNumber) ? null : identity.SerialNumber.Trim();
        var inventory = new DahuaInventory(input.Kind, serial ?? input.FallbackId, input.Language);
        var pt = IsPortuguese(input.Language);
        var manufacturer = string.IsNullOrWhiteSpace(identity.Vendor) ? "Intelbras" : identity.Vendor.Trim();
        var model = string.IsNullOrWhiteSpace(identity.DeviceType) ? null : identity.DeviceType.Trim();
        var name = !string.IsNullOrWhiteSpace(identity.MachineName) ? identity.MachineName.Trim() : model ?? "Intelbras";
        var connections = serial is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal) { ["serial"] = serial };

        switch (input.Kind)
        {
            case DahuaDeviceKind.Recorder:
            {
                var root = inventory.Add(new DeviceModel(inventory.RootId, name, DeviceClasses.Nvr, manufacturer, model, identity.SoftwareVersion, null, connections));
                root.Endpoints.Add(new EndpointModel(MainEndpoint, null, [new CapabilityBinding(CapabilityIds.Generic, null, _recorderGeneric)]));
                AddAlarmInputs(root, input.AlarmInputs, pt);
                foreach (var (number, title) in input.Channels.OrderBy(c => c.Number))
                {
                    var cameraName = string.IsNullOrWhiteSpace(title)
                        ? string.Create(CultureInfo.InvariantCulture, $"{(pt ? "Câmera" : "Camera")} {number}")
                        : title.Trim();
                    var camera = inventory.Add(new DeviceModel(
                        string.Create(CultureInfo.InvariantCulture, $"{inventory.RootId}:ch{number}"),
                        cameraName,
                        DeviceClasses.Camera,
                        manufacturer,
                        null,
                        null,
                        inventory.RootId,
                        new Dictionary<string, string>(StringComparer.Ordinal))
                    {
                        Channel = number,
                    });
                    AddCameraEndpoints(camera, input.SmartRules.GetValueOrDefault(number), pt);
                    inventory._byChannel[number] = camera;
                }

                inventory.ImportedChannels = inventory._byChannel.Count;
                break;
            }

            case DahuaDeviceKind.AccessControl:
            {
                var door = inventory.Add(new DeviceModel(inventory.RootId, name, DeviceClasses.Door, manufacturer, model, identity.SoftwareVersion, null, connections)
                {
                    Door = 1,
                });
                door.Endpoints.Add(new EndpointModel(
                    MainEndpoint,
                    pt ? "Porta" : "Door",
                    [
                        CapabilityBinding.Of(CapabilityIds.DoorOpener),
                        CapabilityBinding.Of(CapabilityIds.Contact),
                        new CapabilityBinding(CapabilityIds.Generic, null, _accessGeneric),
                    ]));
                AddAlarmInputs(door, input.AlarmInputs, pt);
                break;
            }

            case DahuaDeviceKind.XpeIntercom:
            {
                var xpe = inventory.Add(new DeviceModel(inventory.RootId, name, DeviceClasses.Intercom, "Intelbras", model, null, null, connections)
                {
                    Door = 1,
                });
                xpe.Endpoints.Add(new EndpointModel(MainEndpoint, pt ? "Relé da porta" : "Door relay", [CapabilityBinding.Of(CapabilityIds.DoorOpener)]));
                break;
            }

            default:
            {
                var camera = inventory.Add(new DeviceModel(inventory.RootId, name, DeviceClasses.Camera, manufacturer, model, identity.SoftwareVersion, null, connections)
                {
                    Channel = 1,
                });
                AddCameraEndpoints(camera, input.SmartRules.GetValueOrDefault(1), pt);
                AddAlarmInputs(camera, input.AlarmInputs, pt);
                inventory._byChannel[1] = camera;
                break;
            }
        }

        return inventory;
    }

    /// <summary>The camera device of an event's 0-based channel index (a camera's own index is 0), or <c>null</c>.</summary>
    public string? CameraForIndex(int? index)
    {
        lock (_lock)
        {
            return _byChannel.GetValueOrDefault((index ?? 0) + 1)?.Id;
        }
    }

    /// <summary>The 1-based video channel of a camera endpoint (<c>main</c> of a camera device), or <c>null</c>.</summary>
    public int? ChannelOf(string externalDeviceId, string endpointKey)
    {
        lock (_lock)
        {
            return string.Equals(endpointKey, MainEndpoint, StringComparison.Ordinal) && _byId.GetValueOrDefault(externalDeviceId) is { Channel: { } channel }
                ? channel
                : null;
        }
    }

    /// <summary>The 1-based door of a door-opener endpoint, or <c>null</c>.</summary>
    public int? DoorOf(string externalDeviceId, string endpointKey)
    {
        lock (_lock)
        {
            return string.Equals(endpointKey, MainEndpoint, StringComparison.Ordinal) && _byId.GetValueOrDefault(externalDeviceId) is { Door: { } door }
                ? door
                : null;
        }
    }

    /// <summary>Whether the device has the endpoint with the capability.</summary>
    public bool Has(string externalDeviceId, string endpointKey, string capabilityId)
    {
        lock (_lock)
        {
            return _byId.GetValueOrDefault(externalDeviceId)?.Endpoints.FirstOrDefault(e => e.Key == endpointKey) is { } endpoint
                && endpoint.Capabilities.Any(c => c.CapabilityId == capabilityId);
        }
    }

    /// <summary>Whether the device has the endpoint (any capability).</summary>
    public bool HasEndpoint(string externalDeviceId, string endpointKey)
    {
        lock (_lock)
        {
            return _byId.GetValueOrDefault(externalDeviceId)?.Endpoints.Any(e => e.Key == endpointKey) == true;
        }
    }

    /// <summary>
    /// Adds a smart-rule endpoint (<c>tripwire</c>, <c>intrusion</c>) to a camera or an alarm-input endpoint
    /// (<c>alarm&lt;n&gt;</c>) to the root device the first time an event names it: the updated device to upsert, or
    /// <c>null</c> when it already has it or the endpoint does not belong on that device.
    /// </summary>
    public DiscoveredDevice? EnsureEndpoint(string externalDeviceId, string endpointKey)
    {
        var pt = IsPortuguese(_language);
        lock (_lock)
        {
            if (!_byId.TryGetValue(externalDeviceId, out var device) || device.Endpoints.Any(e => e.Key == endpointKey))
            {
                return null;
            }

            if (endpointKey is TripwireEndpoint or IntrusionEndpoint)
            {
                if (device.Channel is null)
                {
                    return null;
                }

                device.Endpoints.Add(SmartRuleEndpoint(endpointKey, pt));
                return device.Describe();
            }

            if (AlarmInputOf(endpointKey) is { } input && externalDeviceId == RootId)
            {
                device.Endpoints.Add(AlarmEndpoint(input, pt));
                return device.Describe();
            }

            return null;
        }
    }

    /// <summary>The endpoint key of alarm input <paramref name="input"/> (1-based).</summary>
    public static string AlarmEndpointKey(int input) => string.Create(CultureInfo.InvariantCulture, $"alarm{input}");

    /// <summary>The 1-based alarm input of an <c>alarm&lt;n&gt;</c> endpoint key, or <c>null</c>.</summary>
    public static int? AlarmInputOf(string endpointKey) =>
        endpointKey.StartsWith("alarm", StringComparison.Ordinal)
        && int.TryParse(endpointKey.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var input)
        && input is >= 1 and <= MaxAlarmInputs
            ? input
            : null;

    private static bool IsPortuguese(string language) => string.Equals(language, "pt-BR", StringComparison.OrdinalIgnoreCase);

    private static void AddCameraEndpoints(DeviceModel camera, IReadOnlySet<string>? rules, bool pt)
    {
        camera.Endpoints.Add(new EndpointModel(MainEndpoint, null, [CapabilityBinding.Of(CapabilityIds.Camera), CapabilityBinding.Of(CapabilityIds.Motion)]));
        if (rules?.Contains(LineCrossingRule) == true)
        {
            camera.Endpoints.Add(SmartRuleEndpoint(TripwireEndpoint, pt));
        }

        if (rules?.Contains(IntrusionRule) == true)
        {
            camera.Endpoints.Add(SmartRuleEndpoint(IntrusionEndpoint, pt));
        }
    }

    private static void AddAlarmInputs(DeviceModel device, int count, bool pt)
    {
        for (var input = 1; input <= Math.Min(count, MaxAlarmInputs); input++)
        {
            device.Endpoints.Add(AlarmEndpoint(input, pt));
        }
    }

    private static EndpointModel SmartRuleEndpoint(string key, bool pt) => new(
        key,
        key == TripwireEndpoint ? (pt ? "Linha virtual" : "Line crossing") : (pt ? "Cerca virtual" : "Intrusion area"),
        [CapabilityBinding.Of(CapabilityIds.Motion)]);

    private static EndpointModel AlarmEndpoint(int input, bool pt) => new(
        AlarmEndpointKey(input),
        string.Create(CultureInfo.InvariantCulture, $"{(pt ? "Entrada de alarme" : "Alarm input")} {input}"),
        [CapabilityBinding.Of(CapabilityIds.Contact)]);

    private DeviceModel Add(DeviceModel device)
    {
        _devices.Add(device);
        _byId[device.Id] = device;
        return device;
    }

    private sealed record EndpointModel(string Key, string? Name, IReadOnlyList<CapabilityBinding> Capabilities);

    private sealed class DeviceModel(
        string id,
        string name,
        string deviceClass,
        string manufacturer,
        string? model,
        string? firmware,
        string? parent,
        Dictionary<string, string> connections)
    {
        public string Id { get; } = id;

        /// <summary>The 1-based video channel of a camera, else <c>null</c>.</summary>
        public int? Channel { get; init; }

        /// <summary>The 1-based door of a door opener, else <c>null</c>.</summary>
        public int? Door { get; init; }

        public List<EndpointModel> Endpoints { get; } = [];

        public DiscoveredDevice Describe() => new(
            Id,
            name,
            deviceClass,
            manufacturer,
            model,
            firmware,
            parent,
            connections,
            null,
            [.. Endpoints.Select(e => new DiscoveredEndpoint(e.Key, e.Name, e.Capabilities))]);
    }
}

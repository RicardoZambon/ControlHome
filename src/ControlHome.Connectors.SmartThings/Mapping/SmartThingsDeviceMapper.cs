using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.SmartThings.Model;
using ControlHome.Domain.Devices;

namespace ControlHome.Connectors.SmartThings.Mapping;

/// <summary>
/// Maps a SmartThings device (description + status) to the canonical model: each enabled component becomes an endpoint
/// (<c>main</c> keeps that key), known capabilities become catalogue capabilities, the rest the endpoint's
/// <c>generic</c> capability (from the capability definitions). Disabled components and capabilities
/// (<c>custom.disabledComponents</c>, <c>custom.disabledCapabilities</c>) are hidden. Pure: no I/O, never throws on
/// vendor data — unknown or malformed values degrade to unknown (<c>null</c>) or are skipped.
/// </summary>
internal static class SmartThingsDeviceMapper
{
    public const string MainEndpoint = "main";

    /// <summary>The mapped device, or <c>null</c> when nothing of it can be represented (no usable capability on any component).</summary>
    public static MappedDevice? Map(SmartThingsDevice device, DeviceStatus status, MapContext context) => Map(device, status, context, out _);

    /// <summary>
    /// The mapped device, or <c>null</c> when nothing of it can be represented yet; <paramref name="missingDefinitions"/>
    /// names the capability definitions it waits for either way (a device of custom capabilities only — a community Edge
    /// driver's — has nothing to show until they arrive, so they must still be fetched).
    /// </summary>
    public static MappedDevice? Map(SmartThingsDevice device, DeviceStatus status, MapContext context, out IReadOnlyList<CapabilityRef> missingDefinitions)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(context);

        missingDefinitions = [];
        var main = device.Main;
        if (main is null)
        {
            return null;
        }

        var disabledComponents = Json.Strings(status.Get(main.Id, StCapabilities.DisabledComponents, "disabledComponents")?.Value)
            .ToHashSet(StringComparer.Ordinal);
        var endpoints = new List<DiscoveredEndpoint>();
        var plans = new Dictionary<string, EndpointPlan>(StringComparer.Ordinal);
        var readings = new List<MappedReading>();
        var missing = new List<CapabilityRef>();
        var buttons = new List<ButtonObservation>();
        var windows = new List<PowerConsumptionWindow>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        string? coverClass = null;
        string? capabilityClass = null;
        foreach (var component in device.Components)
        {
            var isMain = ReferenceEquals(component, main);
            if (!isMain && disabledComponents.Contains(component.Id))
            {
                continue;
            }

            var key = Naming.Unique(isMain ? MainEndpoint : Naming.EndpointKey(component.Id), keys);
            var mapping = new ComponentMapper(device, component, status, context, key, isMain);
            mapping.Run();
            missing.AddRange(mapping.Missing);
            if (mapping.Builder.IsEmpty)
            {
                continue;
            }

            endpoints.Add(mapping.Builder.Build());
            plans[key] = mapping.Plan;
            readings.AddRange(mapping.Readings);
            buttons.AddRange(mapping.Buttons);
            windows.AddRange(mapping.Windows);
            coverClass ??= mapping.CoverClass;
            capabilityClass ??= mapping.CapabilityClass;
        }

        missingDefinitions = missing;
        if (endpoints.Count == 0)
        {
            return null;
        }

        // A door or garage cover is always a garage (safety-critical open), whatever the category says.
        var deviceClass = coverClass == DeviceClasses.Garage
            ? DeviceClasses.Garage
            : CategoryClass(main) ?? coverClass ?? capabilityClass;
        var serial = status.Get(main.Id, StCapabilities.DeviceIdentification, "serialNumber")?.Text;
        var connections = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(serial))
        {
            connections["serial"] = serial.Trim();
        }

        var discovered = new DiscoveredDevice(
            device.DeviceId,
            Limit(device.Label ?? device.Name ?? device.DeviceId, DeviceLimits.NameMaxLength)!,
            Limit(deviceClass, DeviceLimits.DeviceClassMaxLength),
            Limit(Manufacturer(device, status, main), DeviceLimits.ManufacturerMaxLength),
            Limit(Model(device, status, main), DeviceLimits.ModelMaxLength),
            Limit(Firmware(device, status, main), DeviceLimits.FirmwareMaxLength),
            device.ParentDeviceId is { } parent && context.KnownDeviceIds?.Contains(parent) == true ? parent : null,
            connections,
            device.RoomId is { } room && context.Rooms.TryGetValue(room, out var roomName) ? roomName : null,
            endpoints);
        return new MappedDevice(device, discovered, readings, AvailabilityOf(device, status, main), plans, missing, buttons, windows);
    }

    private static Availability AvailabilityOf(SmartThingsDevice device, DeviceStatus status, DeviceComponent main)
    {
        var state = device.HealthState ?? status.Get(main.Id, StCapabilities.HealthCheck, "DeviceWatch-DeviceStatus")?.Text;
        return state?.ToUpperInvariant() switch
        {
            "OFFLINE" => Availability.Offline,
            _ => Availability.Online,
        };
    }

    /// <summary>The device class from the main component's categories (the user's choice first).</summary>
    private static string? CategoryClass(DeviceComponent main) =>
        main.Categories.OrderByDescending(c => c.ChosenByUser).Select(c => ClassOf(c.Name)).FirstOrDefault(c => c is not null);

    private static string? ClassOf(string category) =>
        category switch
        {
            "Light" => DeviceClasses.Light,
            "Switch" => DeviceClasses.Switch,
            "SmartPlug" or "Outlet" => DeviceClasses.Outlet,
            "Thermostat" => DeviceClasses.Thermostat,
            "AirConditioner" => DeviceClasses.AirConditioner,
            "Television" or "TV" => DeviceClasses.Tv,
            "Speaker" or "NetworkAudio" or "Receiver" or "Soundbar" => DeviceClasses.Speaker,
            "Washer" => DeviceClasses.Washer,
            "Dryer" => DeviceClasses.Dryer,
            "SmartLock" => DeviceClasses.Lock,
            "GarageDoor" => DeviceClasses.Garage,
            "Blind" => DeviceClasses.Blind,
            "Curtain" => DeviceClasses.Curtain,
            "Doorbell" or "DoorBell" => DeviceClasses.Doorbell,
            "Camera" => DeviceClasses.Camera,
            "Door" => DeviceClasses.Door,
            "Window" => DeviceClasses.Window,
            "ContactSensor" or "MotionSensor" or "PresenceSensor" or "MultiFunctionalSensor" or "LeakSensor" or "SmokeDetector"
                or "TempSensor" or "HumiditySensor" or "COSensor" or "WaterValve" => DeviceClasses.Sensor,
            "Refrigerator" => "refrigerator",
            "Dishwasher" => "dishwasher",
            "Fan" => "fan",
            "AirPurifier" => "air_purifier",
            "RobotCleaner" => "robot_cleaner",
            _ => null,
        };

    private static string? Manufacturer(SmartThingsDevice device, DeviceStatus status, DeviceComponent main) =>
        Blank(device.ViperManufacturer)
        ?? Blank(device.OcfManufacturer)
        ?? Blank(status.Get(main.Id, StCapabilities.Ocf, "mnmn")?.Text)
        ?? (device.DeviceManufacturerCode is { } code && code.Any(char.IsWhiteSpace) ? code : null);

    private static string? Model(SmartThingsDevice device, DeviceStatus status, DeviceComponent main) =>
        Blank(device.ViperModel)
        ?? Blank(status.Get(main.Id, StCapabilities.DeviceIdentification, "modelName")?.Text)
        ?? Blank(status.Get(main.Id, StCapabilities.Ocf, "mnmo")?.Text?.Split('|')[0])
        ?? Blank(device.PresentationId);

    private static string? Firmware(SmartThingsDevice device, DeviceStatus status, DeviceComponent main) =>
        Blank(device.ViperFirmware)
        ?? Blank(status.Get(main.Id, StCapabilities.Ocf, "mnfv")?.Text)
        ?? Blank(status.Get(main.Id, StCapabilities.FirmwareUpdate, "currentVersion")?.Text)
        ?? Blank(status.Get(main.Id, StCapabilities.SamsungFirmwareVersion, "firmwareVersion")?.Text);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Limit(string? value, int length) => value is null ? null : Naming.Limit(value, length);
}

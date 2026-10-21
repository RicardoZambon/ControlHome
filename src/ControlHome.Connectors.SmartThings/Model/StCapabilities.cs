namespace ControlHome.Connectors.SmartThings.Model;

/// <summary>SmartThings capability ids the connector maps to catalogue capabilities (or deliberately ignores).</summary>
internal static class StCapabilities
{
    public const string Switch = "switch";
    public const string SwitchLevel = "switchLevel";
    public const string ColorControl = "colorControl";
    public const string ColorTemperature = "colorTemperature";
    public const string AirConditionerMode = "airConditionerMode";
    public const string AirConditionerFanMode = "airConditionerFanMode";
    public const string ThermostatMode = "thermostatMode";
    public const string ThermostatCoolingSetpoint = "thermostatCoolingSetpoint";
    public const string ThermostatHeatingSetpoint = "thermostatHeatingSetpoint";
    public const string ThermostatSetpointControl = "custom.thermostatSetpointControl";
    public const string TemperatureMeasurement = "temperatureMeasurement";
    public const string RelativeHumidityMeasurement = "relativeHumidityMeasurement";
    public const string ContactSensor = "contactSensor";
    public const string MotionSensor = "motionSensor";
    public const string PresenceSensor = "presenceSensor";
    public const string OccupancySensor = "occupancySensor";
    public const string WaterSensor = "waterSensor";
    public const string SmokeDetector = "smokeDetector";
    public const string Battery = "battery";
    public const string DustSensor = "dustSensor";
    public const string FineDustSensor = "fineDustSensor";
    public const string CarbonDioxideMeasurement = "carbonDioxideMeasurement";
    public const string TvocMeasurement = "tvocMeasurement";
    public const string Button = "button";
    public const string Lock = "lock";
    public const string LockCodes = "lockCodes";
    public const string PowerMeter = "powerMeter";
    public const string EnergyMeter = "energyMeter";
    public const string PowerConsumptionReport = "powerConsumptionReport";
    public const string MediaPlayback = "mediaPlayback";
    public const string AudioVolume = "audioVolume";
    public const string AudioMute = "audioMute";
    public const string MediaInputSource = "mediaInputSource";
    public const string SamsungMediaInputSource = "samsungvd.mediaInputSource";
    public const string MediaTrackControl = "mediaTrackControl";
    public const string TvChannel = "tvChannel";
    public const string LaunchApp = "custom.launchapp";
    public const string WasherOperatingState = "washerOperatingState";
    public const string DryerOperatingState = "dryerOperatingState";
    public const string DishwasherOperatingState = "dishwasherOperatingState";
    public const string SamsungWasherOperatingState = "samsungce.washerOperatingState";
    public const string SamsungDryerOperatingState = "samsungce.dryerOperatingState";
    public const string SamsungDishwasherOperatingState = "samsungce.dishwasherOperatingState";
    public const string RemoteControlStatus = "remoteControlStatus";
    public const string WindowShade = "windowShade";
    public const string WindowShadeLevel = "windowShadeLevel";
    public const string DoorControl = "doorControl";
    public const string GarageDoorControl = "garageDoorControl";
    public const string HealthCheck = "healthCheck";
    public const string DisabledCapabilities = "custom.disabledCapabilities";
    public const string DisabledComponents = "custom.disabledComponents";
    public const string DeviceIdentification = "samsungce.deviceIdentification";
    public const string Ocf = "ocf";
    public const string FirmwareUpdate = "firmwareUpdate";
    public const string SamsungFirmwareVersion = "samsungvd.firmwareVersion";

    /// <summary>
    /// Plumbing, diagnostics and sensitive capabilities: never mapped and never exposed as generic attributes
    /// (<c>lockCodes</c> carries the names of the people with codes).
    /// </summary>
    public static IReadOnlySet<string> Ignored { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Ocf,
        HealthCheck,
        "refresh",
        "execute",
        "configuration",
        "bridge",
        FirmwareUpdate,
        LockCodes,
        DisabledCapabilities,
        DisabledComponents,
        "samsungce.unavailableCapabilities",
        DeviceIdentification,
        "samsungce.driverVersion",
        "samsungce.driverState",
        "samsungce.softwareUpdate",
        "samsungce.softwareVersion",
        "samsungce.dongleSoftwareInstallation",
        "samsungce.quickControl",
        "samsungce.deviceInfoPrivate",
        "samsungce.selfCheck",
        "sec.diagnosticsInformation",
        "sec.wifiConfiguration",
        "sec.calmConnectionCare",
        "custom.deviceReportStateConfiguration",
        "custom.error",
        "custom.tvsearch",
        "custom.accessibility",
        "custom.recording",
        "demandResponseLoadControl",
        SamsungFirmwareVersion,
        "samsungvd.supportsPowerOnByOcf",
        "samsungvd.ambient",
        "samsungvd.ambientContent",
    };

    /// <summary>
    /// Unmapped capabilities that may open or unlock something: their generic attributes are read-only (no generic
    /// command can bypass the safety policy that the catalogue's <c>lock</c>, <c>cover</c> and <c>doorOpener</c> apply).
    /// </summary>
    public static bool IsSensitiveToControl(string capabilityId)
    {
        foreach (var word in (string[])["lock", "door", "garage", "gate", "security", "alarm", "valve", "siren", "keypad", "code"])
        {
            if (capabilityId.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

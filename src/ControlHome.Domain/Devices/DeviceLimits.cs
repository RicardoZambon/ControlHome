namespace ControlHome.Domain.Devices;

/// <summary>Maximum lengths of the device model's text columns (the database schema and input validation share them).</summary>
public static class DeviceLimits
{
    public const int ConnectorKeyMaxLength = 64;
    public const int NameMaxLength = 128;
    public const int StatusMessageMaxLength = 512;
    public const int ExternalIdMaxLength = 256;
    public const int DeviceClassMaxLength = 32;
    public const int ManufacturerMaxLength = 128;
    public const int ModelMaxLength = 128;
    public const int FirmwareMaxLength = 64;
    public const int IconMaxLength = 64;
    public const int EndpointKeyMaxLength = 64;
    public const int CapabilityIdMaxLength = 64;
    public const int AttributeKeyMaxLength = 128;
}

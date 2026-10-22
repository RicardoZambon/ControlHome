namespace ControlHome.Domain.Devices;

/// <summary>
/// Well-known <see cref="Device.DeviceClass"/> values (icons, grouping and the safety policy: <c>cover.open</c> is
/// safety-critical on <see cref="Gate"/> and <see cref="Garage"/>). Connectors may report other lower-case values.
/// </summary>
public static class DeviceClasses
{
    public const string Light = "light";
    public const string Outlet = "outlet";
    public const string Switch = "switch";
    public const string AirConditioner = "air_conditioner";
    public const string Thermostat = "thermostat";
    public const string Door = "door";
    public const string Window = "window";
    public const string Lock = "lock";
    public const string Gate = "gate";
    public const string Garage = "garage";
    public const string Blind = "blind";
    public const string Curtain = "curtain";
    public const string Sensor = "sensor";
    public const string Tv = "tv";
    public const string Speaker = "speaker";
    public const string Washer = "washer";
    public const string Dryer = "dryer";
    public const string Camera = "camera";
    public const string Nvr = "nvr";
    public const string AlarmPanel = "alarm_panel";
    public const string Doorbell = "doorbell";
    public const string Intercom = "intercom";
    public const string Button = "button";
}

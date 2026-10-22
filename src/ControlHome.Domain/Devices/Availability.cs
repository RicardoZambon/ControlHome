namespace ControlHome.Domain.Devices;

/// <summary>Whether a <see cref="Device"/> can be reached. Stored as a string.</summary>
public enum Availability
{
    /// <summary>Not known: never reported, or its integration's runtime is down.</summary>
    Unknown,

    Online,

    Offline,
}

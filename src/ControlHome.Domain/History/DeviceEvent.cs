namespace ControlHome.Domain.History;

/// <summary>
/// A momentary event of an attribute (button press, doorbell ring, camera motion) — table <c>DeviceEvent</c>,
/// append-only, no key: clustered on <c>(TsUtc, AttributeId)</c>; kept <see cref="RetentionSettings.DeviceEventDays"/>.
/// </summary>
public sealed class DeviceEvent
{
    public const int MaxEventTypeLength = 64;

    /// <summary>Payloads longer than this (JSON text) are not stored.</summary>
    public const int MaxPayloadLength = 4000;

    public int AttributeId { get; set; }

    /// <summary>When it happened (stored as <c>datetime2(3)</c>, UTC).</summary>
    public DateTimeOffset TsUtc { get; set; }

    /// <summary>The event name, e.g. <c>double</c> for a button's double press.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>The event's details: a JSON object (<c>json</c> column), never secrets.</summary>
    public string? Payload { get; set; }
}

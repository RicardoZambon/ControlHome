using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace ControlHome.Connectors.Mqtt.Transport;

/// <summary>MQTT topic names (MQTT 3.1.1 §4.7).</summary>
internal static class MqttTopic
{
    /// <summary>The longest topic MQTT can carry, in UTF-8 bytes.</summary>
    public const int MaxLength = 65_535;

    /// <summary>
    /// Whether <paramref name="topic"/> is a topic <em>name</em>: what a device publishes to, what the runtime subscribes to
    /// as an exact topic and publishes commands to. At least one character, no wildcard (<c>+</c>, <c>#</c>), no NUL, at
    /// most <see cref="MaxLength"/> UTF-8 bytes. Device topics come from discovery payloads that any client of the broker
    /// can publish: one that is not a topic name must never reach a SUBSCRIBE (a malformed filter makes the broker drop
    /// the connection — every reconnect would subscribe it again — and <c>#</c> would subscribe to the whole broker).
    /// </summary>
    public static bool IsValidName([NotNullWhen(true)] string? topic) =>
        !string.IsNullOrEmpty(topic)
        && topic.AsSpan().IndexOfAny('+', '#', '\0') < 0
        && Encoding.UTF8.GetByteCount(topic) <= MaxLength;
}

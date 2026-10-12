using System.Text.Json;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Connectors.Abstractions;

/// <summary>A new value for one attribute, addressed by the connector's own keys.</summary>
/// <param name="ExternalDeviceId">The device's <see cref="DiscoveredDevice.ExternalId"/>.</param>
/// <param name="EndpointKey">The endpoint's <see cref="DiscoveredEndpoint.Key"/> (usually <c>main</c>).</param>
/// <param name="CapabilityId">A catalogue capability id (see <c>CapabilityIds</c>) bound to that endpoint.</param>
/// <param name="AttributeKey">An attribute of that capability (or of the endpoint's generic definition).</param>
/// <param name="Value">The value in the attribute's canonical unit; <see cref="StateValue.Null"/> for unknown.</param>
/// <param name="ObservedAtUtc">When the device observed it; <c>null</c> means now.</param>
public sealed record StateReport(
    string ExternalDeviceId,
    string EndpointKey,
    string CapabilityId,
    string AttributeKey,
    StateValue Value,
    DateTimeOffset? ObservedAtUtc);

/// <summary>
/// Something that happened (not a lasting state) on a momentary attribute: a button's <c>press</c> = <c>double</c>, a
/// doorbell ring, a camera's motion event. Events are never deduplicated; they update the attribute's last value and feed
/// the event log and automation triggers.
/// </summary>
/// <param name="AttributeKey">An attribute whose schema is a <c>MomentarySchema</c>.</param>
/// <param name="Event">The event name (one of the schema's <c>Events</c> when it lists any).</param>
/// <param name="Data">Optional event details (a JSON object), never secrets.</param>
/// <param name="OccurredAtUtc">When it happened; <c>null</c> means now.</param>
public sealed record EventReport(
    string ExternalDeviceId,
    string EndpointKey,
    string CapabilityId,
    string AttributeKey,
    string Event,
    JsonElement? Data,
    DateTimeOffset? OccurredAtUtc);

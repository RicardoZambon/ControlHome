using System.Text.Json.Serialization;

namespace ControlHome.Domain.Capabilities;

/// <summary>When a command counts as done.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConfirmationMode>))]
public enum ConfirmationMode
{
    /// <summary>The connector's acknowledgement is final (momentary actions: volume up, snapshot, door opener pulse).</summary>
    [JsonStringEnumMemberName("ackIsFinal")]
    AckIsFinal,

    /// <summary>Done when the device reports the new state, failed after the command's timeout.</summary>
    [JsonStringEnumMemberName("awaitState")]
    AwaitState,
}

/// <summary>One argument of a command, sent as a member of the command's JSON <c>arguments</c> object.</summary>
/// <param name="Key">camelCase member name, unique within the command.</param>
/// <param name="Required">Whether the member must be present (and not <c>null</c>).</param>
/// <param name="Attribute">
/// The attribute this argument sets (e.g. <c>setLevel(level)</c> → <c>level</c>): the endpoint's constraints for that
/// attribute (ranges, enum subsets) also apply to the argument. <c>null</c> when it sets no attribute.
/// </param>
public sealed record ArgumentDefinition(string Key, ValueSchema Schema, bool Required, string? Attribute = null);

/// <summary>An action a capability accepts.</summary>
/// <param name="Name">camelCase, unique within the capability (e.g. <c>turnOn</c>, <c>setLevel</c>).</param>
/// <param name="Arguments">In display order; empty for argument-less commands.</param>
/// <param name="Confirmation">When the command counts as done.</param>
/// <param name="Timeout">
/// How long to wait for confirmation; <c>null</c> uses the connector's default (about 5 s local, 30 s cloud).
/// </param>
/// <param name="SafetyCritical">
/// Always safety-critical (step-up PIN, at-most-once, no retries, audited): <c>lock.unlock</c>, <c>doorOpener.open</c>,
/// <c>alarmPanel.disarm</c>. Commands that are critical only for some device classes use
/// <see cref="SafetyCriticalForDeviceClasses"/>; ask <see cref="IsSafetyCriticalFor"/>.
/// </param>
/// <param name="FailSafe">The safe direction (<c>lock.lock</c>, <c>cover.close</c>): never gated by the safety policy.</param>
public sealed record CommandDefinition(
    string Name,
    IReadOnlyList<ArgumentDefinition> Arguments,
    ConfirmationMode Confirmation,
    TimeSpan? Timeout,
    bool SafetyCritical,
    bool FailSafe)
{
    /// <summary>
    /// Device classes (see <see cref="Devices.DeviceClasses"/>) for which this command is safety-critical even though
    /// <see cref="SafetyCritical"/> is false, e.g. <c>cover.open</c> on a <c>gate</c> or <c>garage</c>.
    /// </summary>
    public IReadOnlyList<string> SafetyCriticalForDeviceClasses { get; init; } = [];

    /// <summary>
    /// A boolean attribute of the same capability that must be <c>true</c> for the command to be accepted (e.g.
    /// <c>applianceCycle.start</c> requires <c>remoteControlEnabled</c>); checked by the command service against the
    /// current state. <c>null</c> when the command is not gated.
    /// </summary>
    public string? GateAttribute { get; init; }

    /// <summary>Whether the command is safety-critical on a device of class <paramref name="deviceClass"/>.</summary>
    public bool IsSafetyCriticalFor(string? deviceClass) =>
        SafetyCritical
        || (deviceClass is not null && SafetyCriticalForDeviceClasses.Contains(deviceClass, StringComparer.Ordinal));

    /// <summary>The argument named <paramref name="key"/>, or <c>null</c>.</summary>
    public ArgumentDefinition? FindArgument(string key) => Arguments.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal));

    public bool Equals(CommandDefinition? other) =>
        other is not null
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && Arguments.SequenceEqual(other.Arguments)
        && Confirmation == other.Confirmation
        && Timeout == other.Timeout
        && SafetyCritical == other.SafetyCritical
        && FailSafe == other.FailSafe
        && SafetyCriticalForDeviceClasses.SequenceEqual(other.SafetyCriticalForDeviceClasses, StringComparer.Ordinal)
        && string.Equals(GateAttribute, other.GateAttribute, StringComparison.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Name, Arguments.Count, Confirmation, Timeout, SafetyCritical, FailSafe, GateAttribute);
}

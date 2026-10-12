namespace ControlHome.Domain.Devices;

/// <summary>
/// One attribute of an <see cref="EndpointCapability"/>. Its identity <see cref="Id"/> is the <c>AttributeId</c> that every
/// state, history and energy table (and every hub delta) keys on.
/// </summary>
public sealed class DeviceAttribute
{
    /// <summary>The <c>AttributeId</c> (identity).</summary>
    public int Id { get; set; }

    public int EndpointCapabilityId { get; set; }

    /// <summary>The attribute key from the capability definition (unique within the endpoint capability).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The persisted current value, if any has been written yet.</summary>
    public AttributeState? State { get; set; }
}

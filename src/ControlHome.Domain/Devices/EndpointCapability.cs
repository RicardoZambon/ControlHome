namespace ControlHome.Domain.Devices;

/// <summary>
/// A capability an <see cref="Endpoint"/> has (at most once per capability id), with the endpoint's own constraints. A
/// capability the connector stops reporting is <see cref="Retired"/>, not deleted, so its attributes keep their history.
/// </summary>
public sealed class EndpointCapability
{
    public int Id { get; set; }

    public Guid EndpointId { get; set; }

    /// <summary>A <c>CapabilityIds</c> value (e.g. <c>thermostat</c>).</summary>
    public string CapabilityId { get; set; } = string.Empty;

    /// <summary>The catalogue version the endpoint was created or last updated with.</summary>
    public int CapabilityVersion { get; set; } = 1;

    /// <summary>
    /// A <c>CapabilityConstraints</c> JSON object (<c>json</c> column): ranges, enum subsets, mode-dependent ranges.
    /// <c>null</c> when the endpoint has the catalogue's full ranges.
    /// </summary>
    public string? ConstraintsJson { get; set; }

    /// <summary>
    /// For the <c>generic</c> capability only: the connector-supplied <c>CapabilityDefinition</c> (<c>json</c> column,
    /// serialized with <c>CapabilityJson.Options</c>).
    /// </summary>
    public string? GenericDefinitionJson { get; set; }

    /// <summary>No longer reported by the connector: hidden from controls, history kept.</summary>
    public bool Retired { get; set; }

    public List<DeviceAttribute> Attributes { get; set; } = [];
}

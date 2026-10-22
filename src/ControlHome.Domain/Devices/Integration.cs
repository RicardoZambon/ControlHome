namespace ControlHome.Domain.Devices;

/// <summary>
/// One configured connection to a connector (e.g. the Simulated home, a SmartThings account, an MQTT broker). Owns its
/// <see cref="Devices"/>: removing the integration removes them.
/// </summary>
public sealed class Integration
{
    public Guid Id { get; set; }

    /// <summary>The connector's <c>IConnectorDescriptor.Key</c> (e.g. <c>simulated</c>).</summary>
    public string ConnectorKey { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>The connector's non-secret configuration (a JSON object, <c>json</c> column), as its config fields describe.</summary>
    public string ConfigJson { get; set; } = "{}";

    /// <summary>
    /// The secret configuration (tokens, passwords, per-device keys) as a JSON object encrypted with Data Protection;
    /// <c>null</c> when there are none. Never returned by the API.
    /// </summary>
    public string? SecretsProtected { get; set; }

    public IntegrationStatus Status { get; set; } = IntegrationStatus.Connecting;

    /// <summary>A short, non-secret explanation of <see cref="Status"/> (e.g. the last error).</summary>
    public string? StatusMessage { get; set; }

    /// <summary>A disabled integration's runtime is not started (its devices stay, availability Unknown).</summary>
    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public List<Device> Devices { get; set; } = [];
}

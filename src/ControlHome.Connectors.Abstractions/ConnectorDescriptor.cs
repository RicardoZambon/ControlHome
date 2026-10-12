using System.Text.Json.Serialization;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Connectors.Abstractions;

/// <summary>
/// What a connector is and how it is configured: shown in the integrations catalogue (<c>GET /api/connectors</c>) and used
/// to render and validate configuration forms with the shared schema-driven form.
/// </summary>
public interface IConnectorDescriptor
{
    /// <summary>Stable, lower-case id (e.g. <c>simulated</c>, <c>mqtt</c>); stored as <c>Integration.ConnectorKey</c>.</summary>
    string Key { get; }

    LocalizedText DisplayName { get; }

    /// <summary>Icon key (lucide name or a prefixed key such as <c>mdi:…</c>).</summary>
    string Icon { get; }

    ConnectorCategory Category { get; }

    /// <summary>
    /// The integration's configuration form. Non-secret values are stored in <c>Integration.ConfigJson</c> and handed to the
    /// runtime as <see cref="IntegrationInstance.Config"/>; <see cref="FieldSchema.Secret"/> values go to the secret store.
    /// </summary>
    IReadOnlyList<FieldSchema> ConfigFields { get; }

    /// <summary>Per-device settings (e.g. a Tuya <c>local_key</c>, a camera password); usually secret. Empty when none.</summary>
    IReadOnlyList<FieldSchema> DeviceConfigFields { get; }

    ConnectorFeatures Features { get; }
}

/// <summary>A user-visible text in both UI languages (connectors are self-contained: their texts do not live in web locales).</summary>
public sealed record LocalizedText(string En, string PtBr)
{
    /// <summary>The text for a UI locale (<c>pt-BR</c> or anything else for English).</summary>
    public string For(string locale) => string.Equals(locale, "pt-BR", StringComparison.OrdinalIgnoreCase) ? PtBr : En;
}

/// <summary>One field of a configuration form or flow form step.</summary>
/// <param name="Key">camelCase member name in the submitted JSON object.</param>
/// <param name="Schema">The value's shape (rendered by <c>SchemaField</c>, validated with <c>FieldSchemaValidator</c>).</param>
/// <param name="Secret">
/// Write-only: stored encrypted, never returned by the API, never logged (registered with the log redactor).
/// </param>
/// <param name="Help">Optional hint shown under the field.</param>
public sealed record FieldSchema(
    string Key,
    LocalizedText Label,
    ValueSchema Schema,
    bool Required,
    bool Secret,
    LocalizedText? Help);

/// <summary>How the integrations catalogue groups a connector.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConnectorCategory>))]
public enum ConnectorCategory
{
    /// <summary>A vendor's devices or cloud (SmartThings, LG ThinQ, Intelbras, Shelly).</summary>
    [JsonStringEnumMemberName("vendor")]
    Vendor,

    /// <summary>A protocol or bridge many devices speak (MQTT, Matter).</summary>
    [JsonStringEnumMemberName("protocol")]
    Protocol,

    /// <summary>Devices that exist only in software (the Simulated home).</summary>
    [JsonStringEnumMemberName("virtual")]
    Virtual,
}

/// <summary>What a connector supports, so the UI and the supervisor know which paths to offer.</summary>
public sealed record ConnectorFeatures
{
    public static ConnectorFeatures None { get; } = new();

    /// <summary>Setup runs through <see cref="IConnectorFactory.CreateSetupFlow"/>; otherwise from <see cref="IConnectorDescriptor.ConfigFields"/>.</summary>
    public bool SetupFlow { get; init; }

    /// <summary>A reauth / replace-token flow exists (devices are kept).</summary>
    public bool Reauth { get; init; }

    /// <summary>The runtime pushes its own inventory with <see cref="IConnectorContext.UpsertDeviceAsync"/>.</summary>
    public bool PushInventory { get; init; }

    /// <summary>New devices can be paired from the app (Zigbee permit-join and the like).</summary>
    public bool PermitJoin { get; init; }

    /// <summary>Needs the internet (a vendor cloud); otherwise works on the LAN only.</summary>
    public bool Cloud { get; init; }

    /// <summary>State comes from polling (see <see cref="AdaptivePoller"/>) rather than pushed events.</summary>
    public bool Polling { get; init; }
}

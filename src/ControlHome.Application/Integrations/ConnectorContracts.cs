using System.Text.Json;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Application.Integrations;

/// <summary>A connector in the integrations catalogue (<c>GET /api/connectors</c>).</summary>
/// <param name="Key">Stable id, e.g. <c>simulated</c>; used as <c>connectorKey</c> when creating an integration.</param>
/// <param name="Icon">Icon key (lucide name, or a prefixed key such as <c>mdi:…</c>).</param>
/// <param name="Category"><c>vendor</c>, <c>protocol</c> or <c>virtual</c>.</param>
/// <param name="ConfigFields">The integration's configuration form (rendered with the shared schema form).</param>
/// <param name="DeviceConfigFields">Per-device settings, usually secret; empty when none.</param>
public sealed record ConnectorDto(
    string Key,
    LocalizedTextDto DisplayName,
    string Icon,
    string Category,
    IReadOnlyList<FieldSchemaDto> ConfigFields,
    IReadOnlyList<FieldSchemaDto> DeviceConfigFields,
    ConnectorFeaturesDto Features);

/// <summary>A text in both UI languages.</summary>
public sealed record LocalizedTextDto(string En, string PtBr);

/// <summary>A form field.</summary>
/// <param name="Key">camelCase member name of the submitted <c>config</c> object.</param>
/// <param name="Schema">The value's shape (discriminator <c>type</c>).</param>
/// <param name="Secret">Write-only: stored encrypted, never returned.</param>
public sealed record FieldSchemaDto(string Key, LocalizedTextDto Label, ValueSchema Schema, bool Required, bool Secret, LocalizedTextDto? Help);

/// <summary>What a connector supports.</summary>
/// <param name="SetupFlow">
/// The connector has a setup flow of its own (several steps); others get a single config form. Either way, set up through
/// <c>POST /api/integrations/flows</c>.
/// </param>
/// <param name="Reauth">The flow can re-authorize an existing integration (<c>POST /api/integrations/{id}/flows/reauth</c>), keeping its devices.</param>
public sealed record ConnectorFeaturesDto(bool SetupFlow, bool Reauth, bool PushInventory, bool PermitJoin, bool Cloud, bool Polling);

/// <summary>Creates an integration from a connector's config fields (<c>POST /api/integrations</c>; no setup flow in 1.2).</summary>
/// <param name="ConnectorKey">A key from <c>GET /api/connectors</c>.</param>
/// <param name="Name">The integration's name (1–128 characters).</param>
/// <param name="Config">
/// The config object (members per the connector's config fields); secret fields are stored encrypted and never returned.
/// </param>
public sealed record CreateIntegrationRequest(string ConnectorKey, string Name, JsonElement? Config);

/// <summary>The connectors registered in this build (<c>AddConnector&lt;T&gt;()</c>), by key.</summary>
public sealed class ConnectorCatalog
{
    private readonly Dictionary<string, IConnectorFactory> _factories;

    public ConnectorCatalog(IEnumerable<IConnectorFactory> factories)
    {
        ArgumentNullException.ThrowIfNull(factories);
        _factories = new Dictionary<string, IConnectorFactory>(StringComparer.Ordinal);
        foreach (var factory in factories)
        {
            if (!_factories.TryAdd(factory.Descriptor.Key, factory))
            {
                throw new InvalidOperationException($"Two connectors use the key '{factory.Descriptor.Key}'.");
            }
        }
    }

    public IReadOnlyCollection<IConnectorFactory> All => _factories.Values;

    public IConnectorFactory? Find(string key) => _factories.GetValueOrDefault(key);

    /// <summary>The catalogue's DTOs, by display name (English).</summary>
    public IReadOnlyList<ConnectorDto> ToDtos() =>
        _factories.Values
            .Select(f => ToDto(f.Descriptor))
            .OrderBy(d => d.DisplayName.En, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Key, StringComparer.Ordinal)
            .ToList();

    public static ConnectorDto ToDto(IConnectorDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new ConnectorDto(
            descriptor.Key,
            ToDto(descriptor.DisplayName),
            descriptor.Icon,
            descriptor.Category switch
            {
                ConnectorCategory.Vendor => "vendor",
                ConnectorCategory.Protocol => "protocol",
                _ => "virtual",
            },
            descriptor.ConfigFields.Select(ToDto).ToList(),
            descriptor.DeviceConfigFields.Select(ToDto).ToList(),
            new ConnectorFeaturesDto(
                descriptor.Features.SetupFlow,
                descriptor.Features.Reauth,
                descriptor.Features.PushInventory,
                descriptor.Features.PermitJoin,
                descriptor.Features.Cloud,
                descriptor.Features.Polling));
    }

    public static LocalizedTextDto ToDto(LocalizedText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new LocalizedTextDto(text.En, text.PtBr);
    }

    public static FieldSchemaDto ToDto(FieldSchema field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return new FieldSchemaDto(field.Key, ToDto(field.Label), field.Schema, field.Required, field.Secret, field.Help is null ? null : ToDto(field.Help));
    }
}

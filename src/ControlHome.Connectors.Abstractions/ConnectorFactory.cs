using System.Text.Json;

namespace ControlHome.Connectors.Abstractions;

/// <summary>
/// The entry point of a connector, registered statically by the Api (<c>AddConnector&lt;T&gt;()</c>; never loaded as a
/// plugin). One factory serves every integration of its connector.
/// </summary>
public interface IConnectorFactory
{
    IConnectorDescriptor Descriptor { get; }

    /// <summary>
    /// A new setup flow, or <c>null</c> when integrations are created directly from
    /// <see cref="IConnectorDescriptor.ConfigFields"/> (<see cref="ConnectorFeatures.SetupFlow"/> is false).
    /// </summary>
    IConnectorFlow? CreateSetupFlow();

    /// <summary>
    /// The runtime for one integration. Must not start work or open connections: the supervisor calls
    /// <see cref="IConnectorRuntime.RunAsync"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The configuration is invalid for this connector.</exception>
    IConnectorRuntime CreateRuntime(IntegrationInstance instance);
}

/// <summary>
/// Implemented by the factory of a connector whose devices follow from its configuration alone (a virtual connector such
/// as the Simulated home), so the host can import them before the runtime first runs — the demo home is seeded with its
/// devices already placed on the plan. The runtime still reports the same inventory when it starts.
/// </summary>
public interface IStaticInventorySource
{
    /// <summary>The devices an integration with this configuration has, in the order the runtime reports them.</summary>
    /// <exception cref="ArgumentException">The configuration is invalid for this connector.</exception>
    IReadOnlyList<DiscoveredDevice> DescribeDevices(JsonElement config);
}

/// <summary>What a runtime gets to know about its integration.</summary>
/// <param name="Id">The <c>Integration.Id</c>.</param>
/// <param name="Name">The user's name for the integration (for log messages).</param>
/// <param name="Config">The non-secret configuration (a JSON object matching the descriptor's config fields).</param>
/// <param name="Secrets">The integration's secrets, decrypted on demand.</param>
public sealed record IntegrationInstance(Guid Id, string Name, JsonElement Config, ISecretStore Secrets);

/// <summary>
/// Secrets of one integration (tokens, passwords) and its devices (per-device keys), encrypted at rest with Data
/// Protection. Values are registered with the log redactor when read or written; never log them.
/// </summary>
public interface ISecretStore
{
    /// <summary>An integration-level secret (a <see cref="FieldSchema.Secret"/> config field), or <c>null</c>.</summary>
    ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Stores (or, with <c>null</c>, removes) an integration-level secret, e.g. a rotated refresh token. Persisted before
    /// the call completes, so a crash right after a token rotation cannot lose the new token.
    /// </summary>
    ValueTask SetAsync(string key, string? value, CancellationToken cancellationToken);

    /// <summary>A per-device secret (a <see cref="IConnectorDescriptor.DeviceConfigFields"/> field), or <c>null</c>.</summary>
    ValueTask<string?> GetDeviceSecretAsync(string externalDeviceId, string key, CancellationToken cancellationToken);
}

/// <summary>A secret store without secrets (connectors that need none, tests).</summary>
public sealed class EmptySecretStore : ISecretStore
{
    public static EmptySecretStore Instance { get; } = new();

    public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);

    public ValueTask SetAsync(string key, string? value, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This integration has no secret store.");

    public ValueTask<string?> GetDeviceSecretAsync(string externalDeviceId, string key, CancellationToken cancellationToken) =>
        ValueTask.FromResult<string?>(null);
}

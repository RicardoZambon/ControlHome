using ControlHome.Connectors.Abstractions;

namespace ControlHome.Application.Integrations;

/// <summary>
/// The secrets of one integration in clear: integration-level values (tokens, passwords) and per-device values by external
/// device id. Only ever held in memory; registered with the log redactor whenever they are decrypted.
/// </summary>
public sealed record IntegrationSecrets(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Devices)
{
    public static IntegrationSecrets Empty { get; } = new(
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal));

    public bool IsEmpty => Values.Count == 0 && Devices.Count == 0;
}

/// <summary>
/// Encrypts and decrypts an integration's secrets for <c>Integration.SecretsProtected</c> (implemented with Data
/// Protection by Infrastructure; every value that passes through is registered with the log redactor).
/// </summary>
public interface IIntegrationSecretProtector
{
    /// <summary>Integration-level secrets encrypted for <c>Integration.SecretsProtected</c>, or <c>null</c> when there are none.</summary>
    string? Protect(IReadOnlyDictionary<string, string> secrets);

    /// <summary>All of an integration's secrets encrypted, or <c>null</c> when there are none.</summary>
    string? Protect(IntegrationSecrets secrets);

    /// <summary>The secrets in <paramref name="protectedSecrets"/> (empty for <c>null</c>).</summary>
    /// <exception cref="System.Security.Cryptography.CryptographicException">They cannot be decrypted (wrong or lost key ring).</exception>
    IntegrationSecrets Read(string? protectedSecrets);

    /// <summary>
    /// <paramref name="protectedSecrets"/> with its integration-level values replaced by <paramref name="values"/> (a flow's
    /// result: values it no longer returns are removed); per-device values are kept. <c>null</c> when nothing is left.
    /// </summary>
    string? ReplaceValues(string? protectedSecrets, IReadOnlyDictionary<string, string> values);
}

/// <summary>Opens the secret store of a saved integration (for flows that re-authorize or reconfigure it).</summary>
public interface IIntegrationSecretStores
{
    /// <summary>A store reading (and, for rotated tokens, writing) <c>Integration.SecretsProtected</c> of the integration.</summary>
    ISecretStore Open(Guid integrationId);
}

/// <summary>Secret stores of hosts without a device runtime: integrations have no secrets there.</summary>
public sealed class NoIntegrationSecretStores : IIntegrationSecretStores
{
    public static NoIntegrationSecretStores Instance { get; } = new();

    public ISecretStore Open(Guid integrationId) => EmptySecretStore.Instance;
}

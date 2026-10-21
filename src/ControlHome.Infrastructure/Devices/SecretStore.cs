using System.Security.Cryptography;
using System.Text.Json;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Integrations;
using ControlHome.Application.Security;
using ControlHome.Connectors.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Infrastructure.Devices;

/// <summary>
/// The secret configuration of one integration as stored (encrypted) in <c>Integration.SecretsProtected</c>:
/// integration-level values and per-device values, by key.
/// </summary>
public sealed class SecretBag
{
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, Dictionary<string, string>> Devices { get; set; } = new(StringComparer.Ordinal);

    public IEnumerable<string> AllValues() => Values.Values.Concat(Devices.Values.SelectMany(d => d.Values));
}

/// <summary>
/// Encrypts and decrypts <see cref="SecretBag"/>s with Data Protection (purpose <see cref="Purpose"/>, the app's key ring,
/// which is itself encrypted with the data-folder certificate). Every value that passes through is registered with
/// <see cref="KnownSecrets"/>, so it is masked in logs.
/// </summary>
public sealed class IntegrationSecretProtector(IDataProtectionProvider provider, KnownSecrets knownSecrets) : IIntegrationSecretProtector
{
    public const string Purpose = "ControlHome.Integrations.v1";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string? Protect(IReadOnlyDictionary<string, string> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        if (secrets.Count == 0)
        {
            return null;
        }

        var bag = new SecretBag();
        foreach (var (key, value) in secrets)
        {
            bag.Values[key] = value;
        }

        return Protect(bag);
    }

    public string Protect(SecretBag bag)
    {
        ArgumentNullException.ThrowIfNull(bag);
        Register(bag);
        return _protector.Protect(JsonSerializer.Serialize(bag, _json));
    }

    /// <summary>The bag in <paramref name="protectedSecrets"/> (empty for <c>null</c>).</summary>
    /// <exception cref="CryptographicException">The value cannot be decrypted (wrong or lost key ring).</exception>
    public SecretBag Unprotect(string? protectedSecrets)
    {
        if (string.IsNullOrEmpty(protectedSecrets))
        {
            return new SecretBag();
        }

        var bag = JsonSerializer.Deserialize<SecretBag>(_protector.Unprotect(protectedSecrets), _json) ?? new SecretBag();
        Register(bag);
        return bag;
    }

    private void Register(SecretBag bag)
    {
        foreach (var value in bag.AllValues())
        {
            knownSecrets.Register(value);
        }
    }
}

/// <summary>
/// <see cref="ISecretStore"/> of one integration: reads and writes <c>Integration.SecretsProtected</c> through
/// <see cref="IntegrationSecretProtector"/>. Writes are saved before they return (a rotated refresh token is never lost).
/// Once the run it belongs to is stopped (<see cref="Close"/>) writes are refused, so a runtime that does not stop in time
/// cannot overwrite what its successor saved.
/// </summary>
public sealed class SecretStore(Guid integrationId, IServiceScopeFactory scopes, IntegrationSecretProtector protector) : ISecretStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _closed;

    /// <summary>Refuses every later write (<see cref="ObjectDisposedException"/>).</summary>
    public void Close() => _closed = true;

    public async ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var bag = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return bag.Values.GetValueOrDefault(key);
    }

    public async ValueTask SetAsync(string key, string? value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ObjectDisposedException.ThrowIf(_closed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IControlHomeDb>();
            var integration = await db.Integrations.SingleOrDefaultAsync(i => i.Id == integrationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The integration no longer exists.");
            var bag = protector.Unprotect(integration.SecretsProtected);
            if (value is null)
            {
                bag.Values.Remove(key);
            }
            else
            {
                bag.Values[key] = value;
            }

            integration.SecretsProtected = bag.Values.Count == 0 && bag.Devices.Count == 0 ? null : protector.Protect(bag);
            ObjectDisposedException.ThrowIf(_closed, this);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<string?> GetDeviceSecretAsync(string externalDeviceId, string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(externalDeviceId);
        ArgumentException.ThrowIfNullOrEmpty(key);
        var bag = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return bag.Devices.TryGetValue(externalDeviceId, out var values) ? values.GetValueOrDefault(key) : null;
    }

    private async Task<SecretBag> LoadAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IControlHomeDb>();
        var protectedSecrets = await db.Integrations
            .AsNoTracking()
            .Where(i => i.Id == integrationId)
            .Select(i => i.SecretsProtected)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return protector.Unprotect(protectedSecrets);
    }
}

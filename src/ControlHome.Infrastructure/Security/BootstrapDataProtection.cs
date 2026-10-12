using ControlHome.Infrastructure.Configuration;
using Microsoft.AspNetCore.DataProtection;

namespace ControlHome.Infrastructure.Security;

/// <summary>
/// A small Data Protection key ring for secrets needed <em>before</em> the database exists (the main key ring lives in the
/// database): application name <c>ControlHome.Bootstrap</c>, keys in <c>{DataPath}/keys/bootstrap</c>, encrypted at rest
/// with the same certificate as the main key ring (<see cref="DataProtectionCertificateStore"/>). Nothing is read or
/// written until a protector is first used.
/// </summary>
public sealed class BootstrapDataProtection(DataDirectory dataDirectory, DataProtectionCertificateStore certificates)
{
    public const string ApplicationName = "ControlHome.Bootstrap";

    /// <summary>The key ring folder, relative to the data folder.</summary>
    public static readonly string KeysSubdirectory = Path.Combine(DataDirectory.Keys, "bootstrap");

    private readonly Lock _lock = new();
    private IDataProtectionProvider? _provider;

    /// <summary>Where the key ring lives (the folder may not exist yet).</summary>
    public string KeysPath => dataDirectory.GetPath(KeysSubdirectory);

    public IDataProtector CreateProtector(string purpose) => Provider.CreateProtector(purpose);

    private IDataProtectionProvider Provider
    {
        get
        {
            lock (_lock)
            {
                return _provider ??= Create();
            }
        }
    }

    private IDataProtectionProvider Create()
    {
        var keys = dataDirectory.EnsureSubdirectory(KeysSubdirectory);
        var certificate = certificates.Certificate;
        return DataProtectionProvider.Create(
            new DirectoryInfo(keys),
            builder => builder
                .SetApplicationName(ApplicationName)
                .ProtectKeysWithCertificate(certificate)
                .UnprotectKeysWithAnyCertificate(certificate));
    }
}

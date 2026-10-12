using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ControlHome.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace ControlHome.Infrastructure.Security;

/// <summary>
/// The certificate that encrypts the Data Protection key ring at rest (the key ring itself lives in the database, so a
/// database leak alone exposes nothing). Stored at <c>{DataPath}/keys/dataprotection.pfx</c>, generated on first use
/// (RSA 3072, <c>CN=ControlHome Data Protection</c>, valid 100 years, no password, owner-only file mode on Unix).
/// Nothing is read or written until <see cref="Certificate"/> is first used.
/// </summary>
public sealed partial class DataProtectionCertificateStore(
    DataDirectory dataDirectory,
    TimeProvider timeProvider,
    ILogger<DataProtectionCertificateStore> logger) : IDisposable
{
    public const string FileName = "dataprotection.pfx";
    public const string SubjectName = "CN=ControlHome Data Protection";

    private readonly Lock _lock = new();
    private X509Certificate2? _certificate;

    /// <summary>Where the certificate lives (the file may not exist yet).</summary>
    public string CertificatePath => Path.Combine(dataDirectory.GetPath(DataDirectory.Keys), FileName);

    /// <summary>Loads the certificate, generating it on first use.</summary>
    public X509Certificate2 Certificate
    {
        get
        {
            lock (_lock)
            {
                return _certificate ??= LoadOrCreate();
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _certificate?.Dispose();
            _certificate = null;
        }
    }

    private X509Certificate2 LoadOrCreate()
    {
        dataDirectory.EnsureSubdirectory(DataDirectory.Keys);
        var path = CertificatePath;
        if (!File.Exists(path))
        {
            Create(path);
        }

        // Ephemeral keys keep the private key out of the OS key store (no leftovers under the user profile on Windows).
        var flags = OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
        return X509CertificateLoader.LoadPkcs12FromFile(path, password: null, flags);
    }

    private void Create(string path)
    {
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(SubjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        var now = timeProvider.GetUtcNow();
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(100));
        var pfx = certificate.Export(X509ContentType.Pkcs12);

        // Write to a temp file first, then move into place, so a crash never leaves a truncated certificate behind and a
        // concurrent first start keeps whichever file landed first.
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(temp, fileOptions))
            {
                stream.Write(pfx);
            }

            File.Move(temp, path, overwrite: false);
            LogCreated(path, certificate.Thumbprint);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another process created it first: use theirs.
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Generated the Data Protection certificate at {CertificatePath} (thumbprint {Thumbprint}); include it in backups: without it the stored keys cannot be decrypted")]
    private partial void LogCreated(string certificatePath, string thumbprint);
}

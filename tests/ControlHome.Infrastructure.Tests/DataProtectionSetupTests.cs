using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ControlHome.Infrastructure.Persistence;
using ControlHome.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace ControlHome.Infrastructure.Tests;

public sealed class DataProtectionSetupTests : IDisposable
{
    private readonly string _dataPath = Directory.CreateTempSubdirectory("controlhome-dp-").FullName;

    private string CertificatePath => Path.Combine(_dataPath, "keys", DataProtectionCertificateStore.FileName);

    [Fact]
    public async Task Registration_and_container_build_touch_no_files()
    {
        await using var services = Build();

        services.GetRequiredService<DataProtectionCertificateStore>().CertificatePath.ShouldBe(CertificatePath);
        Directory.Exists(Path.Combine(_dataPath, "keys")).ShouldBeFalse();
    }

    [Fact]
    public async Task Key_management_uses_the_certificate_and_the_readiness_gated_database_repository()
    {
        await using var services = Build();

        var options = services.GetRequiredService<IOptions<KeyManagementOptions>>().Value;

        options.XmlEncryptor.ShouldBeOfType<CertificateXmlEncryptor>();
        options.XmlRepository.ShouldBeOfType<ReadinessGatedXmlRepository>();
        File.Exists(CertificatePath).ShouldBeTrue();
    }

    [Fact]
    public async Task Repository_refuses_access_until_the_database_is_ready()
    {
        await using var services = Build();
        var repository = services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository!;

        Should.Throw<DatabaseNotReadyException>(() => repository.GetAllElements()).State.ShouldBe(DatabaseState.NotConfigured);
    }

    [Fact]
    public async Task Certificate_is_generated_once_as_specified_and_reused()
    {
        await using var services = Build();
        var store = services.GetRequiredService<DataProtectionCertificateStore>();

        var certificate = store.Certificate;

        certificate.Subject.ShouldBe(DataProtectionCertificateStore.SubjectName);
        certificate.HasPrivateKey.ShouldBeTrue();
        using (var rsa = certificate.GetRSAPublicKey())
        {
            rsa.ShouldNotBeNull().KeySize.ShouldBe(3072);
        }

        (certificate.NotAfter - certificate.NotBefore).TotalDays.ShouldBeGreaterThan(365 * 99);
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(CertificatePath).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        await using var other = Build();
        other.GetRequiredService<DataProtectionCertificateStore>().Certificate.Thumbprint.ShouldBe(certificate.Thumbprint);
    }

    [Fact]
    public void Deferred_registration_mirrors_the_framework_methods()
    {
        using var key = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=probe", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));

        var descriptors = DeferredCertificateKeyProtection.Describe(certificate);

        // ProtectKeysWithCertificate (encryptor + decryption certificate) and UnprotectKeysWithAnyCertificate.
        descriptors.Count.ShouldBeGreaterThanOrEqualTo(2);
        descriptors.ShouldAllBe(d => d.ServiceType.IsConstructedGenericType);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataPath, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private ServiceProvider Build() =>
        InfrastructureHost.Build(
            new Dictionary<string, string?> { ["ControlHome:DataPath"] = _dataPath },
            services => services.AddControlHomeDataProtection());
}

public sealed class ReadinessGatedXmlRepositoryTests
{
    [Fact]
    public void Passes_through_once_ready()
    {
        var inner = Substitute.For<Microsoft.AspNetCore.DataProtection.Repositories.IXmlRepository>();
        var readiness = new DatabaseReadiness();
        var repository = new ReadinessGatedXmlRepository(inner, readiness);

        Should.Throw<DatabaseNotReadyException>(() => repository.GetAllElements());
        readiness.SetState(DatabaseState.Ready);
        repository.GetAllElements();

        inner.Received(1).GetAllElements();
    }
}

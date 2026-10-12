using ControlHome.Infrastructure.Persistence;
using ControlHome.Infrastructure.Security;
using ControlHome.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Api.IntegrationTests;

/// <summary>The key ring lives in ch.DataProtectionKeys, encrypted with the certificate in {DataPath}/keys.</summary>
public sealed class DataProtectionTests(ControlHomeApiFactory factory) : IClassFixture<ControlHomeApiFactory>
{
    [Fact]
    public async Task Keys_are_stored_in_the_database_encrypted_with_the_data_path_certificate()
    {
        var protector = factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("ControlHome.Tests");

        var protectedPayload = protector.Protect("secret value");

        protector.Unprotect(protectedPayload).ShouldBe("secret value");
        File.Exists(Path.Combine(factory.DataPath, "keys", DataProtectionCertificateStore.FileName)).ShouldBeTrue();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlHomeDbContext>();
        var keys = await db.DataProtectionKeys.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        keys.ShouldNotBeEmpty();
        keys.ShouldAllBe(k => k.Xml!.Contains("encryptedSecret") && k.Xml.Contains("X509Certificate"));
        keys.ShouldAllBe(k => !k.Xml!.Contains("<masterKey"));
    }

    [Fact]
    public async Task A_restarted_app_with_the_same_database_and_certificate_can_unprotect()
    {
        var payload = factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("ControlHome.Tests").Protect("still here");

        // A second host: same database and data path, fresh process state (new key ring cache, certificate loaded from disk).
        using var restarted = factory.WithWebHostBuilder(_ => { });
        await restarted.WaitUntilReadyAsync(ControlHomeApiFactory.ReadyTimeout);

        restarted.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("ControlHome.Tests").Unprotect(payload)
            .ShouldBe("still here");
    }
}

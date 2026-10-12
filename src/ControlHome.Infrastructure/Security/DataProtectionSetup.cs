using ControlHome.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ControlHome.Infrastructure.Security;

public static class DataProtectionSetup
{
    public const string ApplicationName = "ControlHome";

    /// <summary>
    /// Data Protection for cookies and stored secrets: application name <c>ControlHome</c>, key ring persisted in
    /// <c>ch.DataProtectionKeys</c> (<c>PersistKeysToDbContext</c>) and encrypted at rest with the certificate at
    /// <c>{DataPath}/keys/dataprotection.pfx</c> (<c>ProtectKeysWithCertificate</c> + <c>UnprotectKeysWithAnyCertificate</c>,
    /// loaded lazily — see <see cref="DeferredCertificateKeyProtection"/>). The data path comes from
    /// <c>ControlHome:DataPath</c> when first needed, not at registration.
    /// </summary>
    public static IDataProtectionBuilder AddControlHomeDataProtection(this IServiceCollection services)
    {
        services.TryAddSingleton<DataProtectionCertificateStore>();

        var builder = services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .PersistKeysToDbContext<ControlHomeDbContext>();

        DeferredCertificateKeyProtection.Register(builder);

        services.AddOptions<KeyManagementOptions>()
            .PostConfigure<DatabaseReadiness>((options, readiness) =>
            {
                if (options.XmlRepository is { } repository and not ReadinessGatedXmlRepository)
                {
                    options.XmlRepository = new ReadinessGatedXmlRepository(repository, readiness);
                }
            });

        return builder;
    }
}

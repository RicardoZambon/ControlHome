using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ControlHome.Infrastructure.Security;

/// <summary>
/// Applies <see cref="DataProtectionBuilderExtensions.ProtectKeysWithCertificate(IDataProtectionBuilder, X509Certificate2)"/>
/// and <see cref="DataProtectionBuilderExtensions.UnprotectKeysWithAnyCertificate"/> without loading the certificate at
/// registration time. Both framework methods need an <see cref="X509Certificate2"/> up front, but nothing before
/// <c>builder.Build()</c> may touch the disk (and the certificate is generated on first use).
/// </summary>
/// <remarks>
/// The framework registers the decryption certificates on an internal options type, so it cannot be configured directly.
/// Instead the methods are run once against a throwaway in-memory certificate to learn which (options) services they
/// register; each of those is registered here with a factory that re-runs the same methods with the real certificate when
/// Data Protection first resolves its options. The result is exactly what the framework methods would register.
/// </remarks>
internal static class DeferredCertificateKeyProtection
{
    public static void Register(IDataProtectionBuilder builder)
    {
        IReadOnlyList<ServiceDescriptor> probe;
        using (var probeCertificate = CreateProbeCertificate())
        {
            probe = Describe(probeCertificate);
        }

        builder.Services.AddSingleton(sp => new Resolver(sp.GetRequiredService<DataProtectionCertificateStore>(), probe.Count));
        for (var i = 0; i < probe.Count; i++)
        {
            var index = i;
            builder.Services.Add(ServiceDescriptor.Describe(
                probe[i].ServiceType,
                sp => sp.GetRequiredService<Resolver>().Resolve(index, sp),
                probe[i].Lifetime));
        }
    }

    /// <summary>What the framework methods register for <paramref name="certificate"/> (closed option services only).</summary>
    internal static IReadOnlyList<ServiceDescriptor> Describe(X509Certificate2 certificate)
    {
        var services = new ServiceCollection();
        new CapturingBuilder(services).ProtectKeysWithCertificate(certificate).UnprotectKeysWithAnyCertificate(certificate);

        // AddOptions() adds open generic IOptions<> plumbing the host already has; keep only the closed registrations.
        var descriptors = services.Where(d => !d.ServiceType.IsGenericTypeDefinition).ToList();
        foreach (var descriptor in descriptors)
        {
            if (!IsOptionsConfiguration(descriptor.ServiceType))
            {
                throw new InvalidOperationException(
                    $"Unexpected Data Protection registration {descriptor.ServiceType}; review {nameof(DeferredCertificateKeyProtection)}.");
            }
        }

        return descriptors;
    }

    private static bool IsOptionsConfiguration(Type serviceType) =>
        serviceType.IsConstructedGenericType
        && (serviceType.GetGenericTypeDefinition() == typeof(IConfigureOptions<>)
            || serviceType.GetGenericTypeDefinition() == typeof(IPostConfigureOptions<>));

    private static X509Certificate2 CreateProbeCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=ControlHome probe", key, HashAlgorithmName.SHA256);
        var now = DateTimeOffset.UnixEpoch;
        return request.CreateSelfSigned(now, now.AddDays(1));
    }

    private sealed class CapturingBuilder(IServiceCollection services) : IDataProtectionBuilder
    {
        public IServiceCollection Services { get; } = services;
    }

    /// <summary>Re-runs the framework methods with the real certificate the first time any of the services is resolved.</summary>
    private sealed class Resolver(DataProtectionCertificateStore store, int expectedCount)
    {
        private readonly Lazy<IReadOnlyList<ServiceDescriptor>> _descriptors = new(() => Describe(store.Certificate));

        public object Resolve(int index, IServiceProvider services)
        {
            var descriptors = _descriptors.Value;
            if (descriptors.Count != expectedCount)
            {
                throw new InvalidOperationException("Data Protection certificate registrations changed between probe and use.");
            }

            var descriptor = descriptors[index];
            return descriptor.ImplementationInstance
                ?? descriptor.ImplementationFactory?.Invoke(services)
                ?? ActivatorUtilities.CreateInstance(services, descriptor.ImplementationType!);
        }
    }
}

using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlHome.Connectors.LgThinQ.Mqtt;

/// <summary>
/// The MQTT client identity LG issued for an integration: the RSA-2048 private key ControlHome generated, the certificate
/// LG signed for its CSR, and the topic to subscribe to. Kept as one secret value (<see cref="LgSecrets.MqttCredentials"/>)
/// so a re-issue replaces all three atomically. (The SDK issues a new certificate on every start; ControlHome keeps it and
/// re-issues only when the broker rejects it or it is about to expire, at most once a minute.)
/// </summary>
internal sealed record LgMqttCredentials(
    [property: JsonPropertyName("key")] string PrivateKeyPem,
    [property: JsonPropertyName("cert")] string CertificatePem,
    [property: JsonPropertyName("topic")] string Topic,
    [property: JsonPropertyName("issuedAt")] DateTimeOffset IssuedAtUtc)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, _json);

    /// <summary>The stored credentials, or <c>null</c> when absent or unusable (then a new certificate is issued).</summary>
    public static LgMqttCredentials? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var credentials = JsonSerializer.Deserialize<LgMqttCredentials>(json, _json);
            return credentials is { PrivateKeyPem.Length: > 0, CertificatePem.Length: > 0, Topic.Length: > 0 } ? credentials : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>When the certificate expires, or <c>null</c> when it cannot be read.</summary>
    public DateTimeOffset? NotAfter()
    {
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(CertificatePem);
            return new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The client certificate with its private key, usable for a TLS client handshake on every platform.</summary>
    /// <exception cref="CryptographicException">The key and certificate do not match or cannot be read.</exception>
    public X509Certificate2 ClientCertificate()
    {
        var certificate = X509Certificate2.CreateFromPem(CertificatePem, PrivateKeyPem);
        if (!OperatingSystem.IsWindows())
        {
            return certificate;
        }

        // Windows' TLS stack (SChannel) cannot use an ephemeral key for client authentication: round-trip through PKCS#12.
        using (certificate)
        {
            return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
        }
    }

    /// <summary>Prints without the key.</summary>
    public override string ToString() => $"MQTT credentials (topic {Topic}, issued {IssuedAtUtc:u})";
}

/// <summary>The CSR LG's SDK sends: RSA-2048, subject <c>CN=lg_thinq</c>, signed with SHA-512, as base64 DER (no PEM armour).</summary>
internal static class LgCsr
{
    public const int KeySize = 2048;

    /// <summary>A new key (PKCS#8 PEM, to store) and its CSR (to send).</summary>
    public static (string PrivateKeyPem, string Csr) Create()
    {
        using var rsa = RSA.Create(KeySize);
        var request = new CertificateRequest("CN=lg_thinq", rsa, HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1);
        return (rsa.ExportPkcs8PrivateKeyPem(), Convert.ToBase64String(request.CreateSigningRequest()));
    }
}

/// <summary>The trust anchor of LG's MQTT broker (AWS IoT Core): Amazon Root CA 1, bundled with the connector.</summary>
internal static class LgTrust
{
    private static readonly Lazy<X509Certificate2> _amazonRootCa1 = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ControlHome.Connectors.LgThinQ.AmazonRootCA1.crt")
            ?? throw new InvalidOperationException("The bundled Amazon Root CA 1 is missing.");
        using var reader = new StreamReader(stream);
        return X509Certificate2.CreateFromPem(reader.ReadToEnd());
    });

    /// <summary>Amazon Root CA 1 (SHA-256 8E:CD:E6:88:4F:3D:87:B1:12:5B:A3:1A:C3:FC:B1:3D:70:16:DE:7F:57:CC:90:4F:E1:CB:97:C6:AE:98:19:6E).</summary>
    public static X509Certificate2 AmazonRootCa1 => _amazonRootCa1.Value;

    /// <summary>
    /// Whether the broker's certificate chains to one of <paramref name="roots"/> (pinned; the system store is not used)
    /// and matches the host name. Revocation is not checked (AWS's OCSP endpoints may be unreachable from a LAN).
    /// </summary>
    public static bool Validate(X509Certificate? certificate, X509Chain? presented, System.Net.Security.SslPolicyErrors errors, X509Certificate2Collection roots)
    {
        if (certificate is null
            || errors.HasFlag(System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable)
            || errors.HasFlag(System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            return false;
        }

        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(roots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (presented is not null)
        {
            foreach (var element in presented.ChainElements)
            {
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }

        return chain.Build(leaf);
    }
}

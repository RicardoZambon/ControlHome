using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ControlHome.Connectors.LgThinQ.Mqtt;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MQTTnet;
using MQTTnet.Formatter;

namespace ControlHome.Connectors.Tests.LgThinQ;

/// <summary>
/// Mosquitto 2.1.2 as a stand-in for LG's AWS IoT broker: TLS only on 8883, client certificates required
/// (<c>require_certificate true</c>, <c>use_identity_as_username true</c>), a CA, server certificate and client
/// certificates generated per run (never stored), no persistence. The host port is fixed for the container's lifetime, so a
/// restart keeps the address the runtime uses.
/// </summary>
public sealed class LgMosquittoFixture : IAsyncLifetime
{
    /// <summary>Docker Hub publishes 2.1.2 only with the <c>-alpine</c> suffix (the image Umbrel's Mosquitto app ships).</summary>
    public const string Image = "eclipse-mosquitto:2.1.2-alpine";

    private static readonly TimeSpan _ready = TimeSpan.FromSeconds(30);
    private IContainer? _container;

    internal TestCertificateAuthority Ca { get; } = new();

    public string Host => _container?.Hostname ?? throw new InvalidOperationException("The broker is not started.");

    public int Port { get; private set; }

    public async ValueTask InitializeAsync()
    {
        string[] names = ["localhost", "host.docker.internal", Environment.GetEnvironmentVariable("TESTCONTAINERS_HOST_OVERRIDE") ?? "localhost"];
        var (serverCertificate, serverKey) = Ca.ServerCertificate(names);
        const string Config = "listener 8883\ncafile /mosquitto/certs/ca.crt\ncertfile /mosquitto/certs/server.crt\nkeyfile /mosquitto/certs/server.key\n"
            + "require_certificate true\nuse_identity_as_username true\nallow_anonymous false\npersistence false\n";
        var script = string.Join(
            " && ",
            "mkdir -p /mosquitto/certs",
            "cp /tmp/lg/ca.crt /tmp/lg/server.crt /tmp/lg/server.key /mosquitto/certs/",
            "chown -R mosquitto:mosquitto /mosquitto/certs",
            "chmod 0600 /mosquitto/certs/server.key",
            "exec mosquitto -c /tmp/lg/lg.conf");

        for (var attempt = 1; ; attempt++)
        {
            Port = RandomNumberGenerator.GetInt32(20_000, 40_000);
            var container = new ContainerBuilder(Image)
                .WithPortBinding(Port, 8883)
                .WithResourceMapping(Encoding.UTF8.GetBytes(Ca.CertificatePem), "/tmp/lg/ca.crt")
                .WithResourceMapping(Encoding.UTF8.GetBytes(serverCertificate), "/tmp/lg/server.crt")
                .WithResourceMapping(Encoding.UTF8.GetBytes(serverKey), "/tmp/lg/server.key")
                .WithResourceMapping(Encoding.UTF8.GetBytes(Config), "/tmp/lg/lg.conf")
                .WithEntrypoint("sh", "-c")
                .WithCommand(script)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("mosquitto version .* running"))
                .Build();
            try
            {
                await container.StartAsync();
                _container = container;
                break;
            }
            catch (Exception) when (attempt < 5)
            {
                // Most likely the random host port is taken: try another one.
                await container.DisposeAsync();
            }
        }

        await WaitUntilAcceptingAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        Ca.Dispose();
    }

    /// <summary>Stops and starts the broker (clients are disconnected; sessions and subscriptions are lost).</summary>
    public async Task RestartAsync()
    {
        var container = _container ?? throw new InvalidOperationException("The broker is not started.");
        await container.StopAsync();
        await container.StartAsync();
        await WaitUntilAcceptingAsync();
    }

    /// <summary>A client certificate signed by the test CA (as LG signs a CSR), with its key.</summary>
    internal X509Certificate2 ClientCertificate(string commonName = "lg_thinq")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1);
        var pem = Ca.SignCsr(Convert.ToBase64String(request.CreateSigningRequest()));
        return new LgMqttCredentials(key.ExportPkcs8PrivateKeyPem(), pem, "unused", DateTimeOffset.UtcNow).ClientCertificate();
    }

    /// <summary>A plain MQTT client (a stand-in for LG's cloud publishing to the integration's topic).</summary>
    internal async Task<IMqttClient> PublisherAsync()
    {
        var certificate = ClientCertificate("lg-cloud-test");
        var roots = new X509Certificate2Collection(Ca.Certificate);
        var client = new MqttClientFactory().CreateMqttClient();
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(Host, Port)
            .WithClientId("lg-cloud-" + Guid.NewGuid().ToString("N")[..8])
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithTlsOptions(tls => tls
                .UseTls()
                .WithTargetHost(Host)
                .WithClientCertificates([certificate])
                .WithCertificateValidationHandler(args => LgTrust.Validate(args.Certificate, args.Chain, args.SslPolicyErrors, roots)))
            .Build();
        await client.ConnectAsync(options, TestContext.Current.CancellationToken);
        return client;
    }

    private async Task WaitUntilAcceptingAsync()
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var client = await PublisherAsync();
                await client.DisconnectAsync();
                return;
            }
            catch (Exception) when (watch.Elapsed < _ready)
            {
                await Task.Delay(200);
            }
        }
    }
}

[CollectionDefinition(Name)]
public sealed class LgMosquittoCollection : ICollectionFixture<LgMosquittoFixture>
{
    public const string Name = "LgMosquitto";
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.LgThinQ.Model;
using ControlHome.Connectors.LgThinQ.Mqtt;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Connectors.Tests.LgThinQ;

/// <summary>Loads the fixtures under <c>Fixtures/LgThinQ</c> (see SOURCE.md there).</summary>
internal static class LgFixtures
{
    public static string Text(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "LgThinQ", name));

    public static JsonElement Json(string name) => Parse(Text(name));

    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>The device list entry of <paramref name="deviceId"/> from <c>devices.json</c>.</summary>
    public static LgDeviceInfo Info(string deviceId) => LgDeviceInfo.ParseList(Json("devices.json")).Single(d => d.DeviceId == deviceId);

    /// <summary>Maps a fixture device: <c>{name}.profile.json</c> (+ the AC's energy profile when asked).</summary>
    public static LgDeviceModel Model(string deviceId, string name, string? energyProfile = null, string? parent = null) =>
        LgDeviceMapper.Map(Info(deviceId), Json($"{name}.profile.json"), energyProfile is null ? null : Json(energyProfile), parent);

    /// <summary>An LG REST success answer around <paramref name="response"/>.</summary>
    public static string Ok(string response) => $$"""{"messageId":"test-message-0000000001","timestamp":"2026-10-20T10:00:00.000000","response":{{response}}}""";

    /// <summary>An LG REST error answer.</summary>
    public static string Error(string code, string message = "test error") =>
        $$$"""{"messageId":"test-message-0000000002","timestamp":"2026-10-20T10:00:00.000000","error":{"code":"{{{code}}}","message":"{{{message}}}"}}""";
}

/// <summary>Assertions on mapped LG devices.</summary>
internal static class LgAssertions
{
    public static IReadOnlyList<string> Capabilities(this LgDeviceModel model, string endpoint = "main") =>
        [.. model.Discovered.Endpoints.Single(e => e.Key == endpoint).Capabilities.Select(c => c.CapabilityId)];

    public static CapabilityBinding Binding(this LgDeviceModel model, string capability, string endpoint = "main") =>
        model.Discovered.Endpoints.Single(e => e.Key == endpoint).Capabilities.Single(c => c.CapabilityId == capability);

    public static CapabilityDefinition Generic(this LgDeviceModel model, string endpoint = "main") =>
        model.Binding(CapabilityIds.Generic, endpoint).GenericDefinition!;

    public static AttributeConstraint? Constraint(this LgDeviceModel model, string capability, string attribute, string endpoint = "main") =>
        CapabilityConstraints.Parse(model.Binding(capability, endpoint).Constraints).For(attribute);

    public static StateValue? Value(this IReadOnlyList<LgReading> readings, string capability, string attribute, string endpoint = "main") =>
        readings.LastOrDefault(r => r.Endpoint == endpoint && r.Capability == capability && r.Attribute == attribute)?.Value;

    /// <summary>Plans one command (must be accepted) and returns its calls' payloads as JSON text.</summary>
    public static IReadOnlyList<string> Calls(this LgDeviceModel model, JsonElement status, params (string Endpoint, string Capability, string Command, string Arguments)[] commands)
    {
        var (calls, rejection) = model.Plan(Batch(model, commands), status);
        rejection.ShouldBeNull();
        return [.. calls!.Select(c => c.Payload.ToJsonString())];
    }

    public static CommandOutcome? Rejection(this LgDeviceModel model, JsonElement status, string endpoint, string capability, string command, string arguments = "{}") =>
        model.Plan(Batch(model, [(endpoint, capability, command, arguments)]), status).Rejection;

    /// <summary>Every reading names an attribute of a capability bound to its endpoint and fits its schema and constraints.</summary>
    public static void ShouldAllFit(this LgDeviceModel model, IEnumerable<LgReading> readings)
    {
        foreach (var reading in readings)
        {
            var endpoint = model.Discovered.Endpoints.SingleOrDefault(e => e.Key == reading.Endpoint);
            endpoint.ShouldNotBeNull($"{model.DeviceId} has no endpoint {reading.Endpoint}");
            var binding = endpoint.Capabilities.SingleOrDefault(c => c.CapabilityId == reading.Capability);
            binding.ShouldNotBeNull($"{model.DeviceId}/{reading.Endpoint} has no {reading.Capability}");
            var definition = binding.GenericDefinition ?? CapabilityCatalog.Find(binding.CapabilityId)!;
            var attribute = definition.FindAttribute(reading.Attribute);
            attribute.ShouldNotBeNull($"{reading.Capability}.{reading.Attribute}");
            var constraint = CapabilityConstraints.Parse(binding.Constraints).For(reading.Attribute);
            ValueSchemaValidator.Accepts(attribute.Schema, reading.Value, constraint)
                .ShouldBeTrue($"{model.DeviceId}/{reading.Endpoint}/{reading.Capability}.{reading.Attribute} = {reading.Value}");
        }
    }

    private static DeviceCommandBatch Batch(LgDeviceModel model, IEnumerable<(string Endpoint, string Capability, string Command, string Arguments)> commands) =>
        new(model.DeviceId, [.. commands.Select(c => new DeviceCommand(c.Endpoint, c.Capability, c.Command, LgFixtures.Parse(c.Arguments)))], "key");
}

/// <summary>A throw-away certificate authority for the tests: signs LG client CSRs and the test broker's certificate.</summary>
internal sealed class TestCertificateAuthority : IDisposable
{
    private readonly RSA _key = RSA.Create(2048);

    public TestCertificateAuthority()
    {
        var request = new CertificateRequest("CN=ControlHome test CA", _key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
    }

    public X509Certificate2 Certificate { get; }

    public string CertificatePem => Certificate.ExportCertificatePem();

    /// <summary>Signs an LG-style CSR (base64 DER) and returns the client certificate as PEM.</summary>
    public string SignCsr(string base64Der, TimeSpan? validity = null)
    {
        var request = CertificateRequest.LoadSigningRequest(Convert.FromBase64String(base64Der), HashAlgorithmName.SHA512, CertificateRequestLoadOptions.Default, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));
        using var certificate = request.Create(Certificate, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.Add(validity ?? TimeSpan.FromDays(1)), Serial());
        return certificate.ExportCertificatePem();
    }

    /// <summary>A TLS server certificate (PEM certificate and PKCS#8 key) for <paramref name="dnsNames"/> and loopback.</summary>
    public (string CertificatePem, string KeyPem) ServerCertificate(IEnumerable<string> dnsNames)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        foreach (var name in dnsNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IPAddress.TryParse(name, out var address))
            {
                names.AddIpAddress(address);
            }
            else
            {
                names.AddDnsName(name);
            }
        }

        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var certificate = request.Create(Certificate, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1), Serial());
        return (certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }

    public void Dispose()
    {
        Certificate.Dispose();
        _key.Dispose();
    }

    private static byte[] Serial() => RandomNumberGenerator.GetBytes(12);
}

/// <summary>An in-memory LG broker: records connections and subscriptions, delivers test messages, drops connections.</summary>
internal sealed class FakeLgBroker
{
    private readonly ConcurrentQueue<LgMqttConnection> _connections = new();
    private readonly ConcurrentQueue<string> _subscriptions = new();
    private readonly Lock _lock = new();
    private FakeLgTransport? _current;

    /// <summary>The next connection attempts fail like this (consumed one per attempt).</summary>
    public ConcurrentQueue<LgMqttFailure> Failures { get; } = new();

    public IReadOnlyList<LgMqttConnection> Connections => [.. _connections];

    public IReadOnlyList<string> Subscriptions => [.. _subscriptions];

    public bool IsConnected
    {
        get
        {
            lock (_lock)
            {
                return _current is { IsConnected: true };
            }
        }
    }

    public LgMqttTransportFactory Factory => connection => new FakeLgTransport(this, connection);

    /// <summary>Delivers a message to the connected client.</summary>
    public async Task PublishAsync(string topic, string payload)
    {
        FakeLgTransport? transport;
        lock (_lock)
        {
            transport = _current;
        }

        if (transport?.MessageReceived is { } handler)
        {
            await handler(LgMqttMessage.FromText(topic, payload));
        }
    }

    /// <summary>Drops the current connection (as AWS IoT does every now and then).</summary>
    public async Task DropAsync()
    {
        FakeLgTransport? transport;
        lock (_lock)
        {
            transport = _current;
            _current = null;
        }

        if (transport is not null)
        {
            await transport.DropAsync();
        }
    }

    private sealed class FakeLgTransport(FakeLgBroker broker, LgMqttConnection connection) : ILgMqttTransport
    {
        private volatile bool _connected;

        public bool IsConnected => _connected;

        public Func<LgMqttMessage, Task>? MessageReceived { get; set; }

        public Func<string, Task>? Disconnected { get; set; }

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            broker._connections.Enqueue(connection);
            if (broker.Failures.TryDequeue(out var failure))
            {
                throw new LgMqttConnectException(failure, $"test failure {failure}");
            }

            _connected = true;
            lock (broker._lock)
            {
                broker._current = this;
            }

            return Task.CompletedTask;
        }

        public Task SubscribeAsync(string topic, CancellationToken cancellationToken)
        {
            broker._subscriptions.Enqueue(topic);
            return Task.CompletedTask;
        }

        public async Task DropAsync()
        {
            _connected = false;
            if (Disconnected is { } handler)
            {
                await handler("dropped by the test");
            }
        }

        public ValueTask DisposeAsync()
        {
            _connected = false;
            lock (broker._lock)
            {
                if (ReferenceEquals(broker._current, this))
                {
                    broker._current = null;
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>An integration's secret store in memory (records writes; values never leave the test).</summary>
internal sealed class MemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    public MemorySecretStore(params (string Key, string Value)[] values)
    {
        foreach (var (key, value) in values)
        {
            _values[key] = value;
        }
    }

    public int Writes { get; private set; }

    public string? this[string key] => _values.GetValueOrDefault(key);

    public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken) => ValueTask.FromResult(_values.GetValueOrDefault(key));

    public ValueTask SetAsync(string key, string? value, CancellationToken cancellationToken)
    {
        Writes++;
        if (value is null)
        {
            _values.TryRemove(key, out _);
        }
        else
        {
            _values[key] = value;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> GetDeviceSecretAsync(string externalDeviceId, string key, CancellationToken cancellationToken) =>
        ValueTask.FromResult<string?>(null);
}

internal static class Waiting
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(15);

    public static async Task UntilAsync(Func<bool> condition, string because, Task? running = null)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (running is { IsFaulted: true })
            {
                await running;
            }

            if (watch.Elapsed > _timeout)
            {
                throw new TimeoutException($"Timed out waiting until {because}.");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    public static JsonObject Object(string json) => (JsonObject)JsonNode.Parse(json)!;
}

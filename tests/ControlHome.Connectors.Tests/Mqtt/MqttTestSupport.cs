using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.Mqtt;
using ControlHome.Connectors.Mqtt.Model;
using ControlHome.Connectors.Mqtt.Transport;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace ControlHome.Connectors.Tests.Mqtt;

/// <summary>Loads the synthetic fixtures under <c>Fixtures/Mqtt</c> (see SOURCE.md there).</summary>
internal static class MqttFixtures
{
    public static string Text(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mqtt", name));

    public static JsonElement Json(string name)
    {
        using var document = JsonDocument.Parse(Text(name));
        return document.RootElement.Clone();
    }

    /// <summary>The Home Assistant discovery configs: config topic → payload text.</summary>
    public static IReadOnlyDictionary<string, string> HaConfigs() =>
        Json("ha-discovery.json").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetRawText(), StringComparer.Ordinal);

    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

/// <summary>A secret store holding just the MQTT password.</summary>
internal sealed class PasswordStore(string? password) : ISecretStore
{
    public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken) =>
        ValueTask.FromResult(key == MqttSettings.PasswordField ? password : null);

    public ValueTask SetAsync(string key, string? value, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<string?> GetDeviceSecretAsync(string externalDeviceId, string key, CancellationToken cancellationToken) =>
        ValueTask.FromResult<string?>(null);
}

/// <summary>Forwards to a <see cref="RecordingContext"/>, but the first <paramref name="failures"/> upserts fail (a database outage).</summary>
internal sealed class FlakyInventoryContext(RecordingContext inner, int failures) : IConnectorContext
{
    private int _failures = failures;

    public Guid IntegrationId => inner.IntegrationId;

    public TimeProvider Time => inner.Time;

    public ILogger Logger => inner.Logger;

    public void ReportState(StateReport report) => inner.ReportState(report);

    public void ReportEvent(EventReport report) => inner.ReportEvent(report);

    public void ReportAvailability(string externalDeviceId, Availability availability, string? reason) =>
        inner.ReportAvailability(externalDeviceId, availability, reason);

    public Task UpsertDeviceAsync(DiscoveredDevice device, CancellationToken cancellationToken) =>
        Interlocked.Decrement(ref _failures) >= 0
            ? Task.FromException(new InvalidOperationException("The database is unavailable."))
            : inner.UpsertDeviceAsync(device, cancellationToken);

    public Task RemoveDeviceAsync(string externalDeviceId, CancellationToken cancellationToken) =>
        inner.RemoveDeviceAsync(externalDeviceId, cancellationToken);

    public Task SyncInventoryAsync(IReadOnlyList<DiscoveredDevice> devices, CancellationToken cancellationToken) =>
        Interlocked.Decrement(ref _failures) >= 0
            ? Task.FromException(new InvalidOperationException("The database is unavailable."))
            : inner.SyncInventoryAsync(devices, cancellationToken);

    public Task ReportEnergyWindowsAsync(IReadOnlyList<EnergyWindowReport> windows, CancellationToken cancellationToken) =>
        inner.ReportEnergyWindowsAsync(windows, cancellationToken);

    public void SetStatus(IntegrationStatus status, string? message) => inner.SetStatus(status, message);
}

/// <summary>Captures every log message (rendered, with exception text) for "never logs the secret" assertions.</summary>
internal sealed class CapturingLogger : ILogger
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyList<string> Messages => [.. _messages];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        _messages.Enqueue($"{logLevel}: {formatter(state, exception)} {exception}");
    }
}

/// <summary>
/// Runs an <see cref="MqttRuntime"/> against a <see cref="FakeBroker"/>, a <see cref="FakeTimeProvider"/> (reconnect
/// back-off) and a <see cref="RecordingContext"/>. Messages are handled on the runtime's own loop, so assertions poll.
/// </summary>
internal sealed class MqttHarness : IAsyncDisposable
{
    public static readonly MqttRuntimeOptions Options = MqttRuntimeOptions.Default with { ReconnectJitter = 0 };

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);
    private readonly CancellationTokenSource _stop = new();

    private MqttHarness(FakeBroker broker, string configJson, string? password, ILogger? logger)
    {
        Broker = broker;
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero));
        Context = new RecordingContext(Time, logger);
        Factory = new MqttConnectorFactory(broker.Factory, Options);
        Runtime = (MqttRuntime)Factory.CreateRuntime(
            new IntegrationInstance(Context.IntegrationId, "MQTT", MqttFixtures.Parse(configJson), new PasswordStore(password)));
    }

    public FakeBroker Broker { get; }

    public FakeTimeProvider Time { get; }

    public RecordingContext Context { get; }

    public MqttConnectorFactory Factory { get; }

    public MqttRuntime Runtime { get; }

    public Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>Only the named adapters: Zigbee2MQTT on <c>zigbee2mqtt</c>, HA discovery on <c>homeassistant</c>, Tasmota.</summary>
    public static string Config(bool zigbee2Mqtt = true, bool homeAssistant = true, bool tasmota = true, string extra = "") =>
        $$"""
        {"host":"broker.test","username":"controlhome",
         "zigbee2mqttBaseTopic":"{{(zigbee2Mqtt ? "zigbee2mqtt" : string.Empty)}}",
         "discoveryPrefix":"{{(homeAssistant ? "homeassistant" : string.Empty)}}",
         "tasmotaDiscovery":{{(tasmota ? "true" : "false")}}{{extra}}}
        """;

    public static async Task<MqttHarness> StartAsync(
        FakeBroker? broker = null,
        string? config = null,
        string? password = "test-password",
        ILogger? logger = null,
        bool waitForConnection = true)
    {
        var harness = new MqttHarness(broker ?? new FakeBroker(), config ?? Config(), password, logger);
        harness.Running = Task.Run(() => harness.Runtime.RunAsync(harness.Context, harness._stop.Token));
        if (waitForConnection)
        {
            await harness.WaitUntilAsync(() => harness.Runtime.Connections > 0, "the runtime is connected and subscribed");
        }

        return harness;
    }

    public async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (Running.IsFaulted)
            {
                await Running;
            }

            if (watch.Elapsed > _timeout)
            {
                throw new TimeoutException($"Timed out waiting until {because}.");
            }

            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Waits until the runtime has handled everything queued so far (a marker message on an unused topic).</summary>
    public async Task SettleAsync()
    {
        await Task.Delay(20, TestContext.Current.CancellationToken);
        var before = Context.States.Count + Context.Devices.Count + Context.Removed.Count + Context.Events.Count;
        await Task.Delay(30, TestContext.Current.CancellationToken);
        var watch = Stopwatch.StartNew();
        while (Context.States.Count + Context.Devices.Count + Context.Removed.Count + Context.Events.Count != before && watch.Elapsed < _timeout)
        {
            before = Context.States.Count + Context.Devices.Count + Context.Removed.Count + Context.Events.Count;
            await Task.Delay(30, TestContext.Current.CancellationToken);
        }
    }

    public DiscoveredDevice Device(string externalId) => Context.Devices.Last(d => d.ExternalId == externalId);

    public StateReport? Latest(string device, string endpoint, string capability, string attribute) =>
        Context.States.LastOrDefault(s => s.ExternalDeviceId == device && s.EndpointKey == endpoint && s.CapabilityId == capability && s.AttributeKey == attribute);

    public Task WaitForStateAsync(string device, string endpoint, string capability, string attribute, StateValue value) =>
        WaitUntilAsync(
            () => Equals(Latest(device, endpoint, capability, attribute)?.Value, value),
            $"{device}/{endpoint}/{capability}.{attribute} = {value}");

    public Task<CommandOutcome> ExecuteAsync(string device, string capability, string command, string arguments = "{}", string endpoint = "main", string? key = null) =>
        Runtime.ExecuteAsync(
            new DeviceCommandBatch(device, [new DeviceCommand(endpoint, capability, command, MqttFixtures.Parse(arguments))], key ?? Guid.NewGuid().ToString("N")),
            TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await Running.WaitAsync(_timeout);
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }

        await Runtime.DisposeAsync();
        _stop.Dispose();
    }
}

/// <summary>Assertions on readings and command plans of mapped devices.</summary>
internal static class ReadingAssertions
{
    public static StateValue? Value(this IReadOnlyList<MqttReading> readings, string endpoint, string capability, string attribute) =>
        readings.OfType<MqttReading.State>().LastOrDefault(r => r.Endpoint == endpoint && r.Capability == capability && r.Attribute == attribute)?.Value;

    public static StateValue? Value(this IReadOnlyList<MqttReading> readings, string capability, string attribute) =>
        readings.Value("main", capability, attribute);

    public static IReadOnlyList<string> Capabilities(this DiscoveredDevice device, string endpoint = "main") =>
        [.. device.Endpoints.Single(e => e.Key == endpoint).Capabilities.Select(c => c.CapabilityId)];

    public static CapabilityBinding Binding(this DiscoveredDevice device, string capability, string endpoint = "main") =>
        device.Endpoints.Single(e => e.Key == endpoint).Capabilities.Single(c => c.CapabilityId == capability);

    public static CapabilityDefinition Generic(this DiscoveredDevice device, string endpoint = "main") =>
        device.Binding(CapabilityIds.Generic, endpoint).GenericDefinition!;

    public static AttributeConstraint? Constraint(this DiscoveredDevice device, string capability, string attribute, string endpoint = "main") =>
        CapabilityConstraints.Parse(device.Binding(capability, endpoint).Constraints).For(attribute);

    /// <summary>Plans one command (must be supported) and returns its publishes.</summary>
    public static IReadOnlyList<MqttPublish> Publishes(this MqttDevice device, string capability, string command, string arguments = "{}", string endpoint = "main")
    {
        var (plan, rejection, _) = device.Plan(new DeviceCommandBatch(
            device.ExternalId,
            [new DeviceCommand(endpoint, capability, command, MqttFixtures.Parse(arguments))],
            "key"));
        rejection.ShouldBeNull();
        return plan!.Publishes;
    }

    public static CommandOutcome? Rejection(this MqttDevice device, string capability, string command, string arguments = "{}", string endpoint = "main") =>
        device.Plan(new DeviceCommandBatch(device.ExternalId, [new DeviceCommand(endpoint, capability, command, MqttFixtures.Parse(arguments))], "key")).Rejection;

    /// <summary>Every state reading names an attribute of a capability bound to its endpoint and fits its schema and constraints.</summary>
    public static void ShouldAllFit(this DiscoveredDevice device, IEnumerable<MqttReading> readings)
    {
        foreach (var reading in readings.OfType<MqttReading.State>())
        {
            var endpoint = device.Endpoints.SingleOrDefault(e => e.Key == reading.Endpoint);
            endpoint.ShouldNotBeNull($"{device.ExternalId} has no endpoint {reading.Endpoint}");
            var binding = endpoint.Capabilities.SingleOrDefault(c => c.CapabilityId == reading.Capability);
            binding.ShouldNotBeNull($"{device.ExternalId}/{reading.Endpoint} has no {reading.Capability}");
            var definition = binding.GenericDefinition ?? CapabilityCatalog.Find(binding.CapabilityId)!;
            var attribute = definition.FindAttribute(reading.Attribute);
            attribute.ShouldNotBeNull($"{reading.Capability}.{reading.Attribute}");
            var constraint = CapabilityConstraints.Parse(binding.Constraints).For(reading.Attribute);
            ValueSchemaValidator.Accepts(attribute.Schema, reading.Value, constraint)
                .ShouldBeTrue($"{device.ExternalId}/{reading.Endpoint}/{reading.Capability}.{reading.Attribute} = {reading.Value}");
        }
    }
}

using System.Collections.Concurrent;
using System.Text.Json;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.Simulated;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using Microsoft.Extensions.Logging;

namespace ControlHome.Api.IntegrationTests.Devices;

/// <summary>What the test connectors saw (registered as a singleton in the devices test host).</summary>
public sealed class ConnectorSpy
{
    private readonly ConcurrentQueue<DeviceCommandBatch> _batches = new();
    private long _burstReports;

    /// <summary>Every batch a <c>spy</c> runtime received, in order.</summary>
    public IReadOnlyList<DeviceCommandBatch> Batches => [.. _batches];

    /// <summary>Burst connector: reports sent so far.</summary>
    public long BurstReports => Interlocked.Read(ref _burstReports);

    public void Record(DeviceCommandBatch batch) => _batches.Enqueue(batch);

    public void CountBurstReport() => Interlocked.Increment(ref _burstReports);

    public void Clear()
    {
        _batches.Clear();
        Interlocked.Exchange(ref _burstReports, 0);
    }
}

/// <summary>
/// Connector <c>spy</c>: the Simulated basic home, recording every command batch it receives (to prove at-most-once
/// delivery and coalescing).
/// </summary>
public sealed class SpyConnectorFactory(ConnectorSpy spy) : IConnectorFactory
{
    public const string Key = "spy";

    public IConnectorDescriptor Descriptor { get; } = new TestDescriptor(Key, SimulatedConnectorDescriptor.Instance.ConfigFields);

    public IConnectorFlow? CreateSetupFlow() => null;

    public IConnectorRuntime CreateRuntime(IntegrationInstance instance) =>
        new SpyRuntime(new SimulatedConnectorFactory().CreateRuntime(instance), spy);

    private sealed class SpyRuntime(IConnectorRuntime inner, ConnectorSpy spy) : IConnectorRuntime
    {
        public Task RunAsync(IConnectorContext context, CancellationToken cancellationToken) => inner.RunAsync(context, cancellationToken);

        public Task<CommandOutcome> ExecuteAsync(DeviceCommandBatch batch, CancellationToken cancellationToken)
        {
            spy.Record(batch);
            return inner.ExecuteAsync(batch, cancellationToken);
        }

        public Task RefreshAsync(string externalDeviceId, CancellationToken cancellationToken) => inner.RefreshAsync(externalDeviceId, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

/// <summary>
/// Connector <c>canary</c>: has a secret config field <c>token</c> and misbehaves with it on purpose — logs it (plain and
/// destructured), fails its first run with it in the exception message, puts it in its status message and in its command
/// rejections — so tests can prove the secret never leaves the process.
/// </summary>
public sealed class CanaryConnectorFactory : IConnectorFactory
{
    public const string Key = "canary";
    public const string DeviceId = "canary-1";

    private readonly ConcurrentDictionary<Guid, int> _runs = new();

    public IConnectorDescriptor Descriptor { get; } = new TestDescriptor(
        Key,
        [
            new FieldSchema("token", new LocalizedText("Token", "Token"), new StringSchema(256), Required: true, Secret: true, null),
            new FieldSchema("label", new LocalizedText("Label", "Rótulo"), new StringSchema(64), Required: false, Secret: false, null),
        ]);

    public IConnectorFlow? CreateSetupFlow() => null;

    public IConnectorRuntime CreateRuntime(IntegrationInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        // Runs are counted when they start: the integration service also creates a runtime (never run) to check the config.
        return new CanaryRuntime(instance, () => _runs.AddOrUpdate(instance.Id, 1, (_, runs) => runs + 1));
    }

    private sealed class CanaryRuntime(IntegrationInstance instance, Func<int> nextRun) : IConnectorRuntime
    {
        private string? _token;

        public async Task RunAsync(IConnectorContext context, CancellationToken cancellationToken)
        {
            var run = nextRun();
            _token = await instance.Secrets.GetAsync("token", cancellationToken);
            // Logs the secret on purpose, as a careless connector would.
            context.Logger.LogInformation("Canary connecting with token {Token}", _token);
            context.Logger.LogWarning("Canary settings {@Settings}", new { Token = _token, Url = $"https://vendor.example/api?access_token={_token}" });
            context.Logger.LogError(
                new InvalidOperationException($"Vendor rejected token {_token}", new HttpRequestException($"401 for {_token}")),
                "Canary login rejected");
            if (run == 1)
            {
                throw new InvalidOperationException($"Login failed for token {_token}");
            }

            context.SetStatus(IntegrationStatus.Degraded, $"Using token {_token}");
            await context.UpsertDeviceAsync(
                new DiscoveredDevice(
                    DeviceId,
                    "Canary switch",
                    DeviceClasses.Outlet,
                    "Test",
                    "Canary",
                    "1.0",
                    null,
                    new Dictionary<string, string>(),
                    null,
                    [new DiscoveredEndpoint(Endpoint.MainKey, null, [CapabilityBinding.Of(CapabilityIds.Switch)])]),
                cancellationToken);
            context.ReportAvailability(DeviceId, Availability.Online, null);
            context.ReportState(new StateReport(DeviceId, Endpoint.MainKey, CapabilityIds.Switch, "on", StateValue.False, null));
            await Task.Delay(Timeout.InfiniteTimeSpan, context.Time, cancellationToken);
        }

        public Task<CommandOutcome> ExecuteAsync(DeviceCommandBatch batch, CancellationToken cancellationToken) =>
            Task.FromResult(CommandOutcome.Reject(CommandRejectionCodes.Refused, $"Token {_token} may not switch"));

        public Task RefreshAsync(string externalDeviceId, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// Connector <c>burst</c>: one device (<c>burst-1</c>) with a switch and a generic capability of <c>attributes</c> read-only
/// numbers, reporting <c>rate</c> values per second (round robin, every value new) for <c>seconds</c> seconds after
/// <c>delaySeconds</c>. Switch commands complete at once and report the new state.
/// </summary>
public sealed class BurstConnectorFactory(ConnectorSpy spy) : IConnectorFactory
{
    public const string Key = "burst";
    public const string DeviceId = "burst-1";

    public IConnectorDescriptor Descriptor { get; } = new TestDescriptor(
        Key,
        [
            new FieldSchema("attributes", new LocalizedText("Attributes", "Atributos"), new NumberSchema(1, 1000, 1, null, Integer: true), true, false, null),
            new FieldSchema("rate", new LocalizedText("Rate", "Taxa"), new NumberSchema(1, 100_000, 1, null, Integer: true), true, false, null),
            new FieldSchema("seconds", new LocalizedText("Seconds", "Segundos"), new NumberSchema(0, 600, null, null), true, false, null),
            new FieldSchema("delaySeconds", new LocalizedText("Delay", "Atraso"), new NumberSchema(0, 600, null, null), false, false, null),
        ]);

    public IConnectorFlow? CreateSetupFlow() => null;

    public IConnectorRuntime CreateRuntime(IntegrationInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var config = instance.Config;
        return new BurstRuntime(
            config.GetProperty("attributes").GetInt32(),
            config.GetProperty("rate").GetInt32(),
            TimeSpan.FromSeconds(config.GetProperty("seconds").GetDouble()),
            TimeSpan.FromSeconds(config.TryGetProperty("delaySeconds", out var delay) ? delay.GetDouble() : 0),
            spy);
    }

    private sealed class BurstRuntime(int attributes, int rate, TimeSpan duration, TimeSpan delay, ConnectorSpy spy) : IConnectorRuntime
    {
        private IConnectorContext? _context;
        private bool _on;

        public async Task RunAsync(IConnectorContext context, CancellationToken cancellationToken)
        {
            var definition = new CapabilityDefinition(
                CapabilityIds.Generic,
                1,
                Enumerable.Range(0, attributes)
                    .Select(i => new AttributeDefinition($"m{i}", new NumberSchema(), null, StateClass.Measurement, AttributeCategory.Diagnostic, false, false))
                    .ToList(),
                []);
            await context.UpsertDeviceAsync(
                new DiscoveredDevice(
                    DeviceId,
                    "Burst sensor",
                    DeviceClasses.Outlet,
                    "Test",
                    "Burst",
                    "1.0",
                    null,
                    new Dictionary<string, string>(),
                    null,
                    [
                        new DiscoveredEndpoint(
                            Endpoint.MainKey,
                            null,
                            [CapabilityBinding.Of(CapabilityIds.Switch), new CapabilityBinding(CapabilityIds.Generic, null, definition)]),
                    ]),
                cancellationToken);
            _context = context;
            context.ReportAvailability(DeviceId, Availability.Online, null);
            context.ReportState(new StateReport(DeviceId, Endpoint.MainKey, CapabilityIds.Switch, "on", StateValue.False, null));
            context.SetStatus(IntegrationStatus.Ok, null);

            await Task.Delay(delay, context.Time, cancellationToken);
            var started = context.Time.GetTimestamp();
            long sent = 0;
            while (true)
            {
                var elapsed = context.Time.GetElapsedTime(started);
                if (elapsed >= duration)
                {
                    break;
                }

                var due = (long)(elapsed.TotalSeconds * rate);
                for (; sent < due; sent++)
                {
                    context.ReportState(new StateReport(
                        DeviceId,
                        Endpoint.MainKey,
                        CapabilityIds.Generic,
                        $"m{sent % attributes}",
                        StateValue.Number(sent),
                        null));
                    spy.CountBurstReport();
                }

                await Task.Delay(TimeSpan.FromMilliseconds(5), context.Time, cancellationToken);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, context.Time, cancellationToken);
        }

        public Task<CommandOutcome> ExecuteAsync(DeviceCommandBatch batch, CancellationToken cancellationToken)
        {
            spy.Record(batch);
            foreach (var command in batch.Commands)
            {
                _on = command.Command switch
                {
                    "turnOn" => true,
                    "turnOff" => false,
                    "toggle" => !_on,
                    _ => _on,
                };
            }

            _context?.ReportState(new StateReport(DeviceId, Endpoint.MainKey, CapabilityIds.Switch, "on", StateValue.Bool(_on), null));
            return Task.FromResult(CommandOutcome.CompletedOutcome);
        }

        public Task RefreshAsync(string externalDeviceId, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// Connector <c>gadgets</c>: a washer (<c>applianceCycle</c>, remote control off), a gate and a blind (both <c>cover</c>,
/// closed). Commands complete at once, report the new state and are recorded by the <see cref="ConnectorSpy"/>.
/// </summary>
public sealed class GadgetsConnectorFactory(ConnectorSpy spy) : IConnectorFactory
{
    public const string Key = "gadgets";
    public const string Washer = "washer-1";
    public const string Gate = "gate-1";
    public const string Blind = "blind-1";

    public IConnectorDescriptor Descriptor { get; } = new TestDescriptor(Key, []);

    public IConnectorFlow? CreateSetupFlow() => null;

    public IConnectorRuntime CreateRuntime(IntegrationInstance instance) => new GadgetsRuntime(spy);

    private sealed class GadgetsRuntime(ConnectorSpy spy) : IConnectorRuntime
    {
        private IConnectorContext? _context;

        public async Task RunAsync(IConnectorContext context, CancellationToken cancellationToken)
        {
            await context.UpsertDeviceAsync(Device(Washer, DeviceClasses.Washer, CapabilityIds.ApplianceCycle), cancellationToken);
            await context.UpsertDeviceAsync(Device(Gate, DeviceClasses.Gate, CapabilityIds.Cover), cancellationToken);
            await context.UpsertDeviceAsync(Device(Blind, DeviceClasses.Blind, CapabilityIds.Cover), cancellationToken);
            _context = context;
            foreach (var device in new[] { Washer, Gate, Blind })
            {
                context.ReportAvailability(device, Availability.Online, null);
            }

            Report(Washer, CapabilityIds.ApplianceCycle, "machineState", StateValue.String("idle"));
            Report(Washer, CapabilityIds.ApplianceCycle, "remoteControlEnabled", StateValue.False);
            foreach (var cover in new[] { Gate, Blind })
            {
                Report(cover, CapabilityIds.Cover, "position", StateValue.Number(0));
                Report(cover, CapabilityIds.Cover, "coverState", StateValue.String("closed"));
            }

            context.SetStatus(IntegrationStatus.Ok, null);
            await Task.Delay(Timeout.InfiniteTimeSpan, context.Time, cancellationToken);
        }

        public Task<CommandOutcome> ExecuteAsync(DeviceCommandBatch batch, CancellationToken cancellationToken)
        {
            spy.Record(batch);
            foreach (var command in batch.Commands)
            {
                switch (command.Command)
                {
                    case "start":
                        Report(batch.ExternalDeviceId, CapabilityIds.ApplianceCycle, "machineState", StateValue.String("running"));
                        break;
                    case "open":
                        Report(batch.ExternalDeviceId, CapabilityIds.Cover, "position", StateValue.Number(100));
                        Report(batch.ExternalDeviceId, CapabilityIds.Cover, "coverState", StateValue.String("open"));
                        break;
                    case "setPosition":
                        Report(batch.ExternalDeviceId, CapabilityIds.Cover, "position", StateValue.Number(command.Arguments.GetProperty("position").GetDouble()));
                        break;
                }
            }

            return Task.FromResult(CommandOutcome.CompletedOutcome);
        }

        public Task RefreshAsync(string externalDeviceId, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static DiscoveredDevice Device(string id, string deviceClass, string capability) => new(
            id,
            id,
            deviceClass,
            "Test",
            "Gadget",
            "1.0",
            null,
            new Dictionary<string, string>(),
            null,
            [new DiscoveredEndpoint(Endpoint.MainKey, null, [CapabilityBinding.Of(capability)])]);

        private void Report(string device, string capability, string attribute, StateValue value) =>
            _context?.ReportState(new StateReport(device, Endpoint.MainKey, capability, attribute, value, null));
    }
}

internal sealed class TestDescriptor(string key, IReadOnlyList<FieldSchema> configFields) : IConnectorDescriptor
{
    public string Key { get; } = key;

    public LocalizedText DisplayName { get; } = new($"Test {key}", $"Teste {key}");

    public string Icon => "flask-conical";

    public ConnectorCategory Category => ConnectorCategory.Virtual;

    public IReadOnlyList<FieldSchema> ConfigFields { get; } = configFields;

    public IReadOnlyList<FieldSchema> DeviceConfigFields { get; } = [];

    public ConnectorFeatures Features { get; } = new() { PushInventory = true };
}

/// <summary>JSON helpers for the device tests.</summary>
internal static class Json
{
    public static JsonElement Of(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ControlHome.Api.IntegrationTests.History;
using ControlHome.Api.Realtime;
using ControlHome.Application.Realtime;
using ControlHome.Application.Runtime;
using ControlHome.Infrastructure.Devices;
using ControlHome.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog.Core;
using Serilog.Events;

namespace ControlHome.Api.IntegrationTests.Devices;

/// <summary>
/// The API with the device runtime tuned for tests (write-behind every 250 ms, fast restarts), the test connectors
/// (<c>spy</c>, <c>canary</c>, <c>burst</c>, <c>gadgets</c>), a recording decorator around the hub publisher and a log capture. Shared by the
/// <see cref="DevicesCollection"/> (tests run one after the other and reset the app first).
/// </summary>
public class DevicesApiFactory(SqlServerFixture sql) : ControlHomeApiFactory(sql)
{
    public RecordingRealtimePublisher Realtime => Services.GetRequiredService<RecordingRealtimePublisher>();

    public LogCapture Logs => Services.GetRequiredService<LogCapture>();

    public ConnectorSpy Spy => Services.GetRequiredService<ConnectorSpy>();

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await WaitForRuntimeAsync(Services);
    }

    /// <summary>Waits until the device runtime of <paramref name="services"/> is Active.</summary>
    public static async Task WaitForRuntimeAsync(IServiceProvider services, RuntimeMode mode = RuntimeMode.Active, int timeoutSeconds = 60)
    {
        var status = services.GetRequiredService<IRuntimeStatus>();
        var deadline = Stopwatch.StartNew();
        while (status.Mode != mode)
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(timeoutSeconds))
            {
                throw new TimeoutException($"The device runtime did not become {mode} (it is {status.Mode}).");
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    protected override void ConfigureSettings(IDictionary<string, string?> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings["RateLimiting:Setup:PermitLimit"] = "100000";
        settings["RateLimiting:Login:PermitLimit"] = "100000";
        settings["DeviceRuntime:FlushInterval"] = "00:00:00.250";
        settings["DeviceRuntime:LeaseCheckInterval"] = "00:00:02";
        settings["DeviceRuntime:LeaseRetryInterval"] = "00:00:01";
        settings["DeviceRuntime:RestartInitialDelay"] = "00:00:00.200";

        // History tests run the rollups and the retention job themselves (deterministically).
        settings["History:MaintenanceEnabled"] = "false";
        settings["History:ShortTermBatch"] = "1.00:00:00";
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ConnectorSpy>();
            services.AddConnector<SpyConnectorFactory>();
            services.AddConnector<CanaryConnectorFactory>();
            services.AddConnector<BurstConnectorFactory>();
            services.AddConnector<GadgetsConnectorFactory>();
            services.AddSingleton<PowerOnlySpy>();
            services.AddConnector<PowerOnlyConnectorFactory>();
            services.AddSingleton<RecordingRealtimePublisher>();
            services.Replace(ServiceDescriptor.Singleton<IRealtimePublisher>(sp => sp.GetRequiredService<RecordingRealtimePublisher>()));
            services.AddSingleton<LogCapture>();
            services.AddSingleton<ILogEventSink>(sp => sp.GetRequiredService<LogCapture>());
        });
    }
}

[CollectionDefinition(Name)]
public sealed class DevicesCollection : ICollectionFixture<DevicesApiFactory>
{
    public const string Name = "Devices";
}

/// <summary>Records every hub message (with a timestamp) and forwards it to the real hub publisher.</summary>
public sealed class RecordingRealtimePublisher(HomeHubPublisher inner) : IRealtimePublisher
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentQueue<Message> _messages = new();

    public IReadOnlyList<Message> Messages => [.. _messages];

    public IReadOnlyList<(long At, StateDelta Delta)> Deltas =>
        [.. _messages.Where(m => m.Payload is IReadOnlyList<StateDelta>).SelectMany(m => ((IReadOnlyList<StateDelta>)m.Payload!).Select(d => (m.At, d)))];

    public IReadOnlyList<CommandStatusDto> CommandStatuses => [.. _messages.Select(m => m.Payload).OfType<CommandStatusDto>()];

    public IReadOnlyList<IntegrationStatusChangedDto> IntegrationStatuses => [.. _messages.Select(m => m.Payload).OfType<IntegrationStatusChangedDto>()];

    public void Clear() => _messages.Clear();

    /// <summary>Every payload serialized as the hub would send it (for "never contains" checks).</summary>
    public string AllPayloadsJson()
    {
        var text = new StringBuilder();
        foreach (var message in _messages)
        {
            text.Append(message.Name).Append(' ').AppendLine(message.Payload is null ? string.Empty : JsonSerializer.Serialize(message.Payload, message.Payload.GetType(), _json));
        }

        return text.ToString();
    }

    public async Task<CommandStatusDto> WaitForCommandAsync(Guid commandId, params string[] statuses)
    {
        CommandStatusDto? found = null;
        await WaitUntilAsync(
            () => (found = CommandStatuses.FirstOrDefault(s => s.CommandId == commandId && statuses.Contains(s.Status))) is not null,
            $"command {commandId} is {string.Join(" or ", statuses)}");
        return found!;
    }

    public static Task WaitUntilAsync(Func<bool> condition, string because, int timeoutSeconds = 15) =>
        WaitUntilAsync(() => Task.FromResult(condition()), because, timeoutSeconds);

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string because, int timeoutSeconds = 15)
    {
        var deadline = Stopwatch.StartNew();
        while (!await condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(timeoutSeconds))
            {
                throw new TimeoutException($"Timed out waiting until {because}.");
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    public Task StateChangedAsync(IReadOnlyList<StateDelta> deltas, CancellationToken cancellationToken) =>
        RecordAsync(HomeHubMessages.StateChanged, deltas, () => inner.StateChangedAsync(deltas, cancellationToken));

    public Task AvailabilityChangedAsync(AvailabilityChangedDto change, CancellationToken cancellationToken) =>
        RecordAsync(HomeHubMessages.AvailabilityChanged, change, () => inner.AvailabilityChangedAsync(change, cancellationToken));

    public Task DevicesChangedAsync(CancellationToken cancellationToken) =>
        RecordAsync(HomeHubMessages.DevicesChanged, null, () => inner.DevicesChangedAsync(cancellationToken));

    public Task IntegrationStatusChangedAsync(IntegrationStatusChangedDto change, CancellationToken cancellationToken) =>
        RecordAsync(HomeHubMessages.IntegrationStatusChanged, change, () => inner.IntegrationStatusChangedAsync(change, cancellationToken));

    public Task CommandStatusAsync(CommandStatusDto status, CancellationToken cancellationToken) =>
        RecordAsync(HomeHubMessages.CommandStatus, status, () => inner.CommandStatusAsync(status, cancellationToken));

    public Task SystemStatusChangedAsync(SystemStatusDto status, CancellationToken cancellationToken) =>
        RecordAsync(HomeHubMessages.SystemStatusChanged, status, () => inner.SystemStatusChangedAsync(status, cancellationToken));

    private Task RecordAsync(string name, object? payload, Func<Task> forward)
    {
        _messages.Enqueue(new Message(Stopwatch.GetTimestamp(), name, payload));
        return forward();
    }

    /// <param name="At">A <see cref="Stopwatch"/> timestamp.</param>
    public sealed record Message(long At, string Name, object? Payload);
}

/// <summary>Captures every log event as rendered text (message, properties, exception) after enrichment.</summary>
public sealed class LogCapture : ILogEventSink
{
    private readonly ConcurrentQueue<string> _events = new();

    public IReadOnlyList<string> Events => [.. _events];

    public void Clear() => _events.Clear();

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        var text = new StringBuilder();
        text.Append(logEvent.RenderMessage(CultureInfo.InvariantCulture));
        foreach (var (name, value) in logEvent.Properties)
        {
            text.Append(" | ").Append(name).Append('=').Append(value.ToString(null, CultureInfo.InvariantCulture));
        }

        if (logEvent.Exception is not null)
        {
            text.Append(" | ").Append(logEvent.Exception);
        }

        _events.Enqueue(text.ToString());
    }
}

using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using ControlHome.Domain.History;
using ControlHome.Infrastructure.Devices;
using ControlHome.Infrastructure.History;
using ControlHome.Infrastructure.Persistence;
using ControlHome.Infrastructure.Tests.Devices;
using ControlHome.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static ControlHome.Infrastructure.Tests.Devices.DeviceDatabase;

namespace ControlHome.Infrastructure.Tests.History;

/// <summary>
/// A migrated database with a smart plug (switch + power + energy), a power-only plug (switch + power) and a temperature
/// sensor, the real write path (<see cref="SqlStateStore"/>), the rollup and the retention job on a fake runtime clock.
/// </summary>
internal sealed class HistoryHarness : IAsyncDisposable
{
    /// <summary>An hour boundary; every test's timeline is built around it.</summary>
    public static readonly DateTimeOffset T0 = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);

    private HistoryHarness(DeviceDatabase database, Device plug, Device powerOnly, Device sensor, HistoryOptions options)
    {
        Database = database;
        Plug = plug;
        PowerOnly = powerOnly;
        Sensor = sensor;
        var connections = database.Services.GetRequiredService<IDatabaseConnectionProvider>();
        var scopes = database.Services.GetRequiredService<IServiceScopeFactory>();
        Options = Microsoft.Extensions.Options.Options.Create(options);
        Store = new SqlStateStore(connections, Time);
        Rollup = new HistoryRollup(scopes, connections, new RuntimeClock(Time), Pending, Options, NullLogger<HistoryRollup>.Instance);
        var clock = new RuntimeClock(Time);
        EnergyWindows = new VendorEnergyWriter(scopes, connections, clock, Rollup, Options, NullLogger<VendorEnergyWriter>.Instance);
        Purge = new RetentionPurge(
            scopes,
            [
                new StateLogPurgeTarget(connections, clock, Options),
                new ShortTermPurgeTarget(connections, clock, Options),
                new DeviceEventPurgeTarget(connections, clock, Options),
                new AuditLogPurgeTarget(connections, Time, Options),
                new OrphanedHistoryPurgeTarget(connections, Options),
            ],
            NullLogger<RetentionPurge>.Instance);
    }

    public DeviceDatabase Database { get; }

    public FakeTimeProvider Time { get; } = new(T0);

    public FakePendingHistory Pending { get; } = new();

    public IOptions<HistoryOptions> Options { get; }

    public SqlStateStore Store { get; }

    public HistoryRollup Rollup { get; }

    /// <summary>Vendor energy windows (the host side of <c>IConnectorContext.ReportEnergyWindowsAsync</c>).</summary>
    public VendorEnergyWriter EnergyWindows { get; }

    public RetentionPurge Purge { get; }

    public Device Plug { get; }

    public Device PowerOnly { get; }

    public Device Sensor { get; }

    public int PlugPower => Attribute(Plug, CapabilityIds.Power, "power").Id;

    public int PlugEnergy => Attribute(Plug, CapabilityIds.Energy, "energy").Id;

    public int PlugOn => Attribute(Plug, CapabilityIds.Switch, "on").Id;

    public int PowerOnlyPower => Attribute(PowerOnly, CapabilityIds.Power, "power").Id;

    public int Temperature => Attribute(Sensor, CapabilityIds.Temperature, "temperature").Id;

    public static async Task<HistoryHarness> CreateAsync(SqlServerFixture sql, CancellationToken ct, Action<HistoryOptions>? configure = null)
    {
        var database = await DeviceDatabase.CreateAsync(sql, ct);
        var integration = Integration();
        var plug = Device(integration.Id, "plug-1", "Plug", CapabilityIds.Switch, CapabilityIds.Power, CapabilityIds.Energy);
        var powerOnly = Device(integration.Id, "plug-2", "Power-only plug", CapabilityIds.Switch, CapabilityIds.Power);
        var sensor = Device(integration.Id, "sensor-1", "Sensor", CapabilityIds.Temperature);
        integration.Devices.AddRange([plug, powerOnly, sensor]);
        await database.WithDbAsync(async db =>
        {
            db.Integrations.Add(integration);
            await db.SaveChangesAsync(ct);
        });

        var options = new HistoryOptions { SettleDelay = TimeSpan.FromMinutes(1), PowerInterpolationLimit = TimeSpan.FromMinutes(1) };
        configure?.Invoke(options);
        return new HistoryHarness(database, plug, powerOnly, sensor, options);
    }

    public static Guid MainEndpoint(Device device) => device.Endpoints.Single().Id;

    /// <summary>Writes numeric readings through the write-behind's store (<c>null</c> = unknown).</summary>
    public Task WriteAsync(int attributeId, params (DateTimeOffset At, double? Value)[] readings) =>
        Store.WriteAsync(
            new StateWriteBatch(
                [],
                [.. readings.Select(r => new PendingHistoryRow(attributeId, r.At, r.Value is { } v ? StateValue.Number(v) : StateValue.Null))],
                []),
            TestContext.Current.CancellationToken);

    /// <summary>Moves the fake clock to <paramref name="now"/> and runs the rollup once.</summary>
    public Task<RollupRunResult> RollUpAtAsync(DateTimeOffset now)
    {
        Time.SetUtcNow(now);
        return Rollup.RunOnceAsync(TestContext.Current.CancellationToken);
    }

    public Task<List<ShortTermStatistic>> ShortTermAsync(int attributeId) =>
        Database.WithDbAsync(db => db.ShortTermStatistics.AsNoTracking().Where(s => s.AttributeId == attributeId).OrderBy(s => s.BucketStartUtc)
            .ToListAsync(TestContext.Current.CancellationToken));

    public Task<List<HourlyStatistic>> HourlyAsync(int attributeId) =>
        Database.WithDbAsync(db => db.HourlyStatistics.AsNoTracking().Where(s => s.AttributeId == attributeId).OrderBy(s => s.BucketStartUtc)
            .ToListAsync(TestContext.Current.CancellationToken));

    public Task<List<EnergyHourly>> EnergyAsync(Guid endpointId) =>
        Database.WithDbAsync(db => db.EnergyHourly.AsNoTracking().Where(e => e.EndpointId == endpointId).OrderBy(e => e.HourStartUtc)
            .ToListAsync(TestContext.Current.CancellationToken));

    public Task<List<StateLogEntry>> StateLogAsync(int attributeId) =>
        Database.WithDbAsync(db => db.StateLog.AsNoTracking().Where(s => s.AttributeId == attributeId).OrderBy(s => s.TsUtc)
            .ToListAsync(TestContext.Current.CancellationToken));

    public Task<DateTimeOffset?> WatermarkAsync(string name) =>
        Database.WithDbAsync(db => db.RollupProgress.AsNoTracking().Where(p => p.Name == name).Select(p => (DateTimeOffset?)p.WatermarkUtc)
            .SingleOrDefaultAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => Database.DisposeAsync();
}

/// <summary>A write-behind stand-in: what it still holds is what the test says.</summary>
internal sealed class FakePendingHistory : IPendingHistory
{
    public DateTimeOffset? OldestPendingHistoryUtc { get; set; }
}

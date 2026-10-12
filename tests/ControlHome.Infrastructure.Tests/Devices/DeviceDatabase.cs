using System.Security.Cryptography;
using ControlHome.Application.Devices;
using ControlHome.Application.Floors;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using ControlHome.Infrastructure.Persistence;
using ControlHome.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace ControlHome.Infrastructure.Tests.Devices;

/// <summary>A migrated test database with the devices and floors services, plus builders for device graphs.</summary>
internal sealed class DeviceDatabase : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan _readyTimeout = TimeSpan.FromSeconds(120);

    private readonly SqlServerFixture _sql;
    private readonly string _name;

    private DeviceDatabase(SqlServerFixture sql, string name, ServiceProvider services)
    {
        _sql = sql;
        _name = name;
        Services = services;
    }

    public ServiceProvider Services { get; }

    public static async Task<DeviceDatabase> CreateAsync(SqlServerFixture sql, CancellationToken ct)
    {
        await sql.EnsureStartedAsync();
        var suffix = RandomNumberGenerator.GetHexString(8, lowercase: true);
        var services = InfrastructureHost.Build(
            new Dictionary<string, string?> { ["ConnectionStrings:ControlHome"] = sql.CreateDatabaseConnectionString(suffix) },
            s =>
            {
                s.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
                s.AddFloors();
                s.AddDevices();
            });
        var database = new DeviceDatabase(sql, sql.DatabaseName(suffix), services);

        var initializer = services.GetRequiredService<DatabaseInitializer>();
        await initializer.StartAsync(ct);
        await initializer.ExecuteTask!.WaitAsync(_readyTimeout, ct);
        await initializer.StopAsync(ct);
        services.GetRequiredService<DatabaseReadiness>().State.ShouldBe(DatabaseState.Ready);
        return database;
    }

    public async Task<T> WithDbAsync<T>(Func<ControlHomeDbContext, Task<T>> work)
    {
        await using var scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<ControlHomeDbContext>());
    }

    public async Task WithDbAsync(Func<ControlHomeDbContext, Task> work)
    {
        await using var scope = Services.CreateAsyncScope();
        await work(scope.ServiceProvider.GetRequiredService<ControlHomeDbContext>());
    }

    public async Task<T> WithAsync<TService, T>(Func<TService, Task<T>> work)
        where TService : notnull
    {
        await using var scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        await _sql.DropDatabaseAsync(_name);
    }

    public static Integration Integration(string name = "Simulated", string connectorKey = "simulated") => new()
    {
        Id = Guid.CreateVersion7(),
        ConnectorKey = connectorKey,
        Name = name,
        ConfigJson = """{"preset":"basic","seed":42}""",
        Status = IntegrationStatus.Ok,
        CreatedAtUtc = Now,
        UpdatedAtUtc = Now,
    };

    /// <summary>A device with a main endpoint holding the given capabilities, each with all its catalogue attributes.</summary>
    public static Device Device(Guid integrationId, string externalId, string name, params string[] capabilityIds) => new()
    {
        Id = Guid.CreateVersion7(),
        IntegrationId = integrationId,
        ExternalId = externalId,
        Name = name,
        Manufacturer = "ControlHome",
        Model = "Simulated " + name,
        ConnectionsJson = $$"""{"serial":"SIM-{{externalId}}"}""",
        Availability = Availability.Online,
        CreatedAtUtc = Now,
        Endpoints =
        [
            new Endpoint
            {
                Id = Guid.CreateVersion7(),
                Key = Endpoint.MainKey,
                Capabilities = capabilityIds.Select(id => new EndpointCapability
                {
                    CapabilityId = id,
                    Attributes = CapabilityCatalog.Find(id)!.Attributes.Select(a => new DeviceAttribute { Key = a.Key }).ToList(),
                }).ToList(),
            },
        ],
    };

    public static DeviceAttribute Attribute(Device device, string capabilityId, string key) =>
        device.Endpoints.SelectMany(e => e.Capabilities).Single(c => c.CapabilityId == capabilityId).Attributes.Single(a => a.Key == key);
}

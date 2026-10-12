using System.Security.Cryptography;
using ControlHome.Application.Runtime;
using ControlHome.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Api.IntegrationTests.Devices;

/// <summary>Two app instances on one database: exactly one runs the device runtime; the other takes over when it stops.</summary>
public sealed class RuntimeLeaseTests(SqlServerFixture sql)
{
    [Fact]
    public async Task A_second_host_on_the_same_database_is_a_standby_until_the_first_stops()
    {
        await sql.EnsureStartedAsync();
        var suffix = RandomNumberGenerator.GetHexString(8, lowercase: true);
        var connectionString = sql.CreateDatabaseConnectionString(suffix);
        var dataPath = Directory.CreateTempSubdirectory("controlhome-lease-").FullName;
        try
        {
            var first = new SharedDatabaseHost(connectionString, dataPath);
            await using (var second = new SharedDatabaseHost(connectionString, dataPath))
            {
                try
                {
                    await first.StartAsync();
                    await DevicesApiFactory.WaitForRuntimeAsync(first.Services, RuntimeMode.Active);

                    await second.StartAsync();
                    await DevicesApiFactory.WaitForRuntimeAsync(second.Services, RuntimeMode.Standby);
                    first.Mode.ShouldBe(RuntimeMode.Active);
                }
                finally
                {
                    await first.DisposeAsync();
                }

                // The first host released the lease when it stopped: the standby takes over.
                await DevicesApiFactory.WaitForRuntimeAsync(second.Services, RuntimeMode.Active, timeoutSeconds: 30);
            }
        }
        finally
        {
            await sql.DropDatabaseAsync(sql.DatabaseName(suffix));
            try
            {
                Directory.Delete(dataPath, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }

    /// <summary>An app instance on an existing database (it is created by whichever host starts first).</summary>
    private sealed class SharedDatabaseHost(string connectionString, string dataPath) : WebApplicationFactory<Program>
    {
        public RuntimeMode Mode => Services.GetRequiredService<IRuntimeStatus>().Mode;

        public async Task StartAsync()
        {
            StartServer();
            await this.WaitUntilReadyAsync(ControlHomeApiFactory.ReadyTimeout);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(ControlHomeApiFactory.TestingEnvironment);
            builder.UseSetting("ConnectionStrings:ControlHome", connectionString);
            builder.UseSetting("ControlHome:DataPath", dataPath);
            builder.UseSetting("Setup:Code", ControlHomeApiFactory.SetupCode);
            builder.UseSetting("DeviceRuntime:LeaseRetryInterval", "00:00:00.500");
            builder.UseSetting("DeviceRuntime:LeaseCheckInterval", "00:00:01");
        }
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using ControlHome.Application.Devices;
using ControlHome.Application.Integrations;
using ControlHome.Application.Runtime;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using ControlHome.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Api.IntegrationTests.Devices;

/// <summary>
/// Two app instances on one database: exactly one runs the device runtime, the other is a read-only standby and takes over
/// when it stops.
/// </summary>
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

                    await StandbyRefusesRuntimeChangesAsync(first, second);
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

    /// <summary>
    /// The standby is read-only: integration changes would never reach the active instance (409 <c>runtime.standby</c>) and
    /// commands cannot be sent from it (503 <c>commands.unavailable</c>); the active instance's integration keeps running.
    /// </summary>
    private static async Task StandbyRefusesRuntimeChangesAsync(SharedDatabaseHost active, SharedDatabaseHost standby)
    {
        var ct = TestContext.Current.CancellationToken;
        using var activeAdmin = await active.CompleteSetupAsync(cancellationToken: ct);
        using var created = await activeAdmin.PostAsJsonAsync(
            "/api/integrations",
            new CreateIntegrationRequest("simulated", "Simulated", Json.Of("""{"preset":"basic"}""")),
            ct);
        await created.ShouldHaveStatusAsync(HttpStatusCode.Created);
        var integration = (await created.Content.ReadFromJsonAsync<IntegrationDto>(ct))!;

        using var standbyAdmin = await standby.LoginAsync(AuthTestExtensions.AdminUserName, AuthTestExtensions.AdminPassword, ct);
        DeviceSummaryDto[] devices = [];
        await RecordingRealtimePublisher.WaitUntilAsync(
            async () => (devices = (await standbyAdmin.GetFromJsonAsync<DeviceSummaryDto[]>("/api/devices", ct))!).Length == 5,
            "the standby reads the devices the active instance imported",
            timeoutSeconds: 30);

        using (var create = await standbyAdmin.PostAsJsonAsync(
            "/api/integrations",
            new CreateIntegrationRequest("simulated", "Second", Json.Of("""{"preset":"basic"}""")),
            ct))
        {
            await create.ShouldBeProblemAsync(HttpStatusCode.Conflict, "runtime.standby");
        }

        using (var delete = await standbyAdmin.DeleteAsync($"/api/integrations/{integration.Id}", ct))
        {
            await delete.ShouldBeProblemAsync(HttpStatusCode.Conflict, "runtime.standby");
        }

        var light = devices.Single(d => d.DeviceClass == DeviceClasses.Light).Endpoints.Single().Id;
        using (var command = await standbyAdmin.PostAsJsonAsync(
            $"/api/endpoints/{light}/commands",
            new CommandRequest(CapabilityIds.Switch, "toggle", null, null),
            ct))
        {
            await command.ShouldBeProblemAsync(HttpStatusCode.ServiceUnavailable, "commands.unavailable");
        }

        (await standbyAdmin.GetFromJsonAsync<IntegrationDto[]>("/api/integrations", ct))!.ShouldHaveSingleItem().Id.ShouldBe(integration.Id);
        active.Services.GetRequiredService<ICommandDispatcher>().IsRunning(integration.Id).ShouldBeTrue();
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

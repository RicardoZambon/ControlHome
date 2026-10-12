using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ControlHome.Application.Setup;
using ControlHome.Infrastructure.Persistence;
using ControlHome.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Api.IntegrationTests;

/// <summary>Pre-database setup mode, the requests that must not configure anything (Task 0.4-B).</summary>
public sealed class DatabaseSetupTests(PreDatabaseApiFactory factory) : IClassFixture<PreDatabaseApiFactory>
{
    private const string Password = "Not-The-Password-42";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ConfigFile => Path.Combine(factory.DataPath, "config", DatabaseConfigFile.FileName);

    [Fact]
    public async Task Status_reports_no_database_and_no_source()
    {
        using var client = factory.CreateApiClient();

        var status = await GetStatusAsync(client);

        status.DatabaseConfigured.ShouldBeFalse();
        status.DatabaseSource.ShouldBe("none");
        status.DatabaseState.ShouldBe(nameof(DatabaseState.NotConfigured));
        status.DatabaseError.ShouldBeNull();
        status.SetupCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task Wrong_setup_code_is_400_setup_invalid_code_and_saves_nothing()
    {
        using var client = factory.CreateApiClient();

        using var response = await client.PostAsJsonAsync("/api/setup/database", Request() with { SetupCode = "WRONG-CODE-0000" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadProblemCodeAsync(Ct)).ShouldBe("setup.invalid_code");
        await ShouldStillBeUnconfiguredAsync(client);
    }

    [Fact]
    public async Task Invalid_body_is_400_validation_failed_with_errors()
    {
        using var client = factory.CreateApiClient();
        var request = new DatabaseSetupRequest(ControlHomeApiFactory.SetupCode, "bad host;x=1", 0, "1st;DROP DATABASE x", "", "", true);

        using var response = await client.PostAsJsonAsync("/api/setup/database", request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.ReadJsonAsync(Ct);
        problem.GetProperty("code").GetString().ShouldBe("validation.failed");
        problem.GetProperty("errors").EnumerateObject().Select(p => p.Name)
            .ShouldBe(["host", "port", "database", "userName", "password"], ignoreOrder: true);
        await ShouldStillBeUnconfiguredAsync(client);
    }

    [Theory]
    [InlineData("1Database")]
    [InlineData("Control-Home")]
    [InlineData("ControlHome\n")]
    [InlineData("[ControlHome]")]
    public async Task Database_names_must_be_plain_identifiers(string database)
    {
        using var client = factory.CreateApiClient();

        using var response = await client.PostAsJsonAsync("/api/setup/database", Request() with { Database = database }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.ReadJsonAsync(Ct);
        problem.GetProperty("errors").EnumerateObject().Select(p => p.Name).ShouldBe(["database"]);
    }

    [Fact]
    public async Task Unreachable_server_is_400_connection_failed_without_echoing_secrets()
    {
        using var client = factory.CreateApiClient();

        // Port 1 on loopback: refused at once, no credentials ever reach a server.
        using var response = await client.PostAsJsonAsync("/api/setup/database", Request(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(Ct);
        using var problem = JsonDocument.Parse(body);
        problem.RootElement.GetProperty("code").GetString().ShouldBe("db.connection_failed");
        problem.RootElement.GetProperty("reason").GetString().ShouldBe("unreachable");
        problem.RootElement.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("127.0.0.1:1");
        body.ShouldNotContain(Password);
        body.ShouldNotContain("Password=", Case.Insensitive);
        body.ShouldNotContain("User ID", Case.Insensitive);
        await ShouldStillBeUnconfiguredAsync(client);
    }

    private static DatabaseSetupRequest Request() =>
        new(ControlHomeApiFactory.SetupCode, "127.0.0.1", 1, "ControlHome_Test_never", "sa", Password, TrustServerCertificate: true);

    private async Task ShouldStillBeUnconfiguredAsync(HttpClient client)
    {
        var status = await GetStatusAsync(client);
        status.DatabaseConfigured.ShouldBeFalse();
        status.DatabaseState.ShouldBe(nameof(DatabaseState.NotConfigured));
        File.Exists(ConfigFile).ShouldBeFalse();
    }

    internal static async Task<SetupStatusDto> GetStatusAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/setup/status", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<SetupStatusDto>(Ct))!;
    }
}

/// <summary>
/// Pre-database setup end to end on its own host: the wizard's connection to the test server is saved, the database is
/// created and migrated without a restart, and setup completes on it.
/// </summary>
public sealed class DatabaseSetupFlowTests(PreDatabaseApiFactory factory) : IClassFixture<PreDatabaseApiFactory>
{
    private static readonly TimeSpan _readyTimeout = TimeSpan.FromSeconds(120);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Valid_connection_is_202_then_Ready_and_a_second_one_is_409()
    {
        var request = await factory.NewDatabaseRequestAsync();
        Assert.SkipWhen(request is null, "The test SQL Server is not reachable by host, port and SQL login.");
        using var client = factory.CreateApiClient();

        using (var response = await client.PostAsJsonAsync("/api/setup/database", request, Ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
            response.Headers.Location?.ToString().ShouldBe("/api/setup/status");
            var accepted = (await response.Content.ReadFromJsonAsync<SetupStatusDto>(Ct))!;
            accepted.DatabaseConfigured.ShouldBeTrue();
            accepted.DatabaseSource.ShouldBe("file");
            accepted.SetupCompleted.ShouldBeFalse();
        }

        var status = await WaitForReadyAsync(client);
        status.DatabaseSource.ShouldBe("file");
        status.DatabaseError.ShouldBeNull();

        // Saved encrypted: none of what was typed is readable in the file.
        var file = await File.ReadAllTextAsync(Path.Combine(factory.DataPath, "config", DatabaseConfigFile.FileName), Ct);
        foreach (var typed in new[] { request.Password, request.UserName, request.Host, request.Database })
        {
            file.ShouldNotContain(typed, Case.Insensitive);
        }

        // Only while nothing is configured.
        using (var again = await client.PostAsJsonAsync("/api/setup/database", request, Ct))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            var body = await again.Content.ReadAsStringAsync(Ct);
            body.ShouldContain("\"code\":\"db.already_configured\"");
            body.ShouldNotContain(request.Password);
        }

        // The app now runs on the database the wizard chose.
        using var admin = await factory.CompleteSetupAsync(cancellationToken: Ct);
        using (var me = await admin.GetAsync("/api/auth/me", Ct))
        {
            me.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await DatabaseSetupTests.GetStatusAsync(client)).SetupCompleted.ShouldBeTrue();
        factory.Services.GetRequiredService<IDatabaseConnectionProvider>().Source.ShouldBe(DatabaseConnectionSource.File);
    }

    private static async Task<SetupStatusDto> WaitForReadyAsync(HttpClient client)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(_readyTimeout);
        while (true)
        {
            var status = await DatabaseSetupTests.GetStatusAsync(client);
            if (status.DatabaseState == nameof(DatabaseState.Ready))
            {
                return status;
            }

            status.DatabaseState.ShouldNotBe(nameof(DatabaseState.Failed), status.DatabaseError);
            await Task.Delay(200, timeout.Token);
        }
    }
}

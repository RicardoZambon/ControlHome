using System.Security.Cryptography;
using ControlHome.Domain.Security;
using ControlHome.Infrastructure.Persistence;
using ControlHome.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace ControlHome.Infrastructure.Tests;

public sealed class DatabaseInitializerTests(SqlServerFixture sql)
{
    private static readonly TimeSpan _readyTimeout = TimeSpan.FromSeconds(120);

    [Fact]
    public async Task Fresh_database_is_created_migrated_and_seeded()
    {
        var ct = TestContext.Current.CancellationToken;
        await sql.EnsureStartedAsync();
        var suffix = NewSuffix();
        var name = sql.DatabaseName(suffix);
        await using var services = InfrastructureHost.Build(Settings(sql.CreateDatabaseConnectionString(suffix)));
        try
        {
            var readiness = await RunInitializerAsync(services, ct);

            readiness.State.ShouldBe(DatabaseState.Ready, readiness.LastError);
            readiness.LastError.ShouldBeNull();

            await using (var scope = services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ControlHomeDbContext>();
                (await db.Roles.Select(r => r.Name).ToListAsync(ct)).ShouldBe(Roles.All, ignoreOrder: true);
                (await db.Database.GetPendingMigrationsAsync(ct)).ShouldBeEmpty();
            }

            var (compatibility, recovery) = await DatabaseSettingsAsync(name, ct);
            compatibility.ShouldBe(170);
            recovery.ShouldBe("SIMPLE");

            var tables = await TablesAsync(name, ct);
            tables.ShouldBe(
                [
                    "auth.RoleClaims", "auth.Roles", "auth.UserClaims", "auth.UserLogins", "auth.UserRoles", "auth.UserTokens",
                    "auth.Users", "ch.AuditLog", "ch.DataProtectionKeys", "ch.Homes", "ch.__EFMigrationsHistory",
                    "ch.Floors", "ch.Rooms", "ch.StoredFiles",
                ],
                ignoreOrder: true);
            (await ColumnTypeAsync(name, "ch.Homes", "Tariff", ct)).ShouldBe("json");
            (await ColumnTypeAsync(name, "ch.AuditLog", "DetailsJson", ct)).ShouldBe("json");
            (await ColumnTypeAsync(name, "ch.Floors", "Background", ct)).ShouldBe("json");
            (await ColumnTypeAsync(name, "ch.Floors", "RowVersion", ct)).ShouldBe("timestamp");
            (await ColumnTypeAsync(name, "ch.Rooms", "Polygon", ct)).ShouldBe("json");
            (await ColumnTypeAsync(name, "ch.StoredFiles", "Data", ct)).ShouldBe("varbinary");
        }
        finally
        {
            await sql.DropDatabaseAsync(name);
        }
    }

    [Fact]
    public async Task Running_again_on_an_existing_database_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await sql.EnsureStartedAsync();
        var suffix = NewSuffix();
        var settings = Settings(sql.CreateDatabaseConnectionString(suffix));
        try
        {
            await using (var first = InfrastructureHost.Build(settings))
            {
                (await RunInitializerAsync(first, ct)).State.ShouldBe(DatabaseState.Ready);
            }

            await using var second = InfrastructureHost.Build(settings);
            (await RunInitializerAsync(second, ct)).State.ShouldBe(DatabaseState.Ready);
            await using var scope = second.CreateAsyncScope();
            (await scope.ServiceProvider.GetRequiredService<ControlHomeDbContext>().Roles.CountAsync(ct)).ShouldBe(Roles.All.Count);
        }
        finally
        {
            await sql.DropDatabaseAsync(sql.DatabaseName(suffix));
        }
    }

    [Fact]
    public async Task Existing_database_below_compatibility_level_170_fails_with_a_clear_code()
    {
        var ct = TestContext.Current.CancellationToken;
        await sql.EnsureStartedAsync();
        var suffix = NewSuffix();
        var connectionString = await sql.CreateEmptyDatabaseAsync(suffix, compatibilityLevel: 160);
        try
        {
            await using var services = InfrastructureHost.Build(Settings(connectionString));

            var readiness = await RunInitializerAsync(services, ct);

            readiness.State.ShouldBe(DatabaseState.Failed);
            readiness.LastError.ShouldBe("sql.compatibility_unsupported");
            (await TablesAsync(sql.DatabaseName(suffix), ct)).ShouldBeEmpty();
        }
        finally
        {
            await sql.DropDatabaseAsync(sql.DatabaseName(suffix));
        }
    }

    [Fact]
    public async Task Missing_database_is_created_under_the_name_the_operator_chose()
    {
        // The name is outside DatabaseNameGuard's tooling pattern, so the fixture could not drop it again: only ever against
        // the throwaway container, never a shared server.
        Assert.SkipWhen(sql.UsesSharedServer, "Runs only against the Testcontainers SQL Server.");
        var ct = TestContext.Current.CancellationToken;
        await sql.EnsureStartedAsync();
        const string ChosenName = "HomeAutomation_Main";
        var connectionString = new SqlConnectionStringBuilder(sql.MasterConnectionString) { InitialCatalog = ChosenName }.ConnectionString;
        await using var services = InfrastructureHost.Build(Settings(connectionString));

        var readiness = await RunInitializerAsync(services, ct);

        readiness.State.ShouldBe(DatabaseState.Ready, readiness.LastError);
        var (compatibility, recovery) = await DatabaseSettingsAsync(ChosenName, ct);
        compatibility.ShouldBe(170);
        recovery.ShouldBe("SIMPLE");
    }

    [Fact]
    public async Task Unreachable_server_is_Connecting_then_Failed_after_the_startup_timeout()
    {
        var ct = TestContext.Current.CancellationToken;
        var settings = Settings("Server=tcp:127.0.0.1,1;Database=ControlHome_Test_unreachable;Connect Timeout=1;Encrypt=False");
        settings["Database:StartupTimeout"] = "00:00:02";
        await using var services = InfrastructureHost.Build(settings);
        var readiness = services.GetRequiredService<DatabaseReadiness>();
        var initializer = services.GetRequiredService<DatabaseInitializer>();

        await initializer.StartAsync(ct);
        try
        {
            readiness.State.ShouldBe(DatabaseState.Connecting);
            await WaitForStateAsync(readiness, DatabaseState.Failed, TimeSpan.FromSeconds(30), ct);
            readiness.LastError.ShouldBe("db.connection_failed");
        }
        finally
        {
            await initializer.StopAsync(ct);
        }
    }

    [Fact]
    public async Task Unexpected_exception_fails_with_connection_failed_instead_of_crashing_the_host()
    {
        var ct = TestContext.Current.CancellationToken;
        var connections = Substitute.For<IDatabaseConnectionProvider>();
        connections.GetConnectionString().Returns(
            _ => "Server=tcp:127.0.0.1,1;Database=ControlHome_Test_unexpected;Encrypt=False",
            _ => throw new InvalidOperationException("unexpected failure while reading the connection"));
        await using var services = InfrastructureHost.Build(
            new Dictionary<string, string?>(),
            s => s.AddSingleton(connections));
        var readiness = services.GetRequiredService<DatabaseReadiness>();
        var initializer = services.GetRequiredService<DatabaseInitializer>();

        await initializer.StartAsync(ct);
        try
        {
            await WaitForStateAsync(readiness, DatabaseState.Failed, TimeSpan.FromSeconds(30), ct);

            // A faulted ExecuteTask would stop the host (BackgroundServiceExceptionBehavior.StopHost): it keeps retrying instead.
            initializer.ExecuteTask!.IsFaulted.ShouldBeFalse();
            readiness.LastError.ShouldBe("db.connection_failed");
        }
        finally
        {
            await initializer.StopAsync(ct);
        }

        initializer.ExecuteTask.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task Without_a_connection_string_it_waits_in_NotConfigured_until_one_is_saved()
    {
        var ct = TestContext.Current.CancellationToken;
        const string ConnectionString = "Server=tcp:127.0.0.1,1;Database=ControlHome_Test_waiting;Encrypt=False";
        var connections = new FakeConnectionProvider();
        var probe = Substitute.For<ISqlServerProbe>();
        probe.ProbeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<SqlServerFacts>(new InvalidOperationException("server down")));
        var time = new RecordingTimeProvider();
        await using var services = InfrastructureHost.Build(
            new Dictionary<string, string?>(),
            s => s.AddSingleton<IDatabaseConnectionProvider>(connections).AddSingleton(probe).AddSingleton<TimeProvider>(time));
        var readiness = services.GetRequiredService<DatabaseReadiness>();
        var initializer = services.GetRequiredService<DatabaseInitializer>();

        await initializer.StartAsync(ct);
        try
        {
            readiness.State.ShouldBe(DatabaseState.NotConfigured);
            await Task.Delay(200, ct);
            initializer.ExecuteTask!.IsCompleted.ShouldBeFalse();
            await probe.DidNotReceive().ProbeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

            connections.Set(ConnectionString);

            // The first attempt ran (and failed): the initializer now waits one second before the next one.
            (await time.NextTimerAsync()).ShouldBe(TimeSpan.FromSeconds(1));
            readiness.State.ShouldBe(DatabaseState.Connecting);
            await probe.Received(1).ProbeAsync(ConnectionString, "ControlHome_Test_waiting", Arg.Any<CancellationToken>());
        }
        finally
        {
            await initializer.StopAsync(ct);
        }
    }

    [Fact]
    public async Task After_the_startup_timeout_it_stays_Failed_and_retries_every_minute()
    {
        var ct = TestContext.Current.CancellationToken;
        var serverAnswers = false;
        var probe = Substitute.For<ISqlServerProbe>();
        probe.ProbeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => serverAnswers
            ? Task.FromResult(new SqlServerFacts(MajorVersion: 16, DatabaseExists: true, CompatibilityLevel: 160, CanCreateDatabase: false, HasDatabaseAccess: true))
            : Task.FromException<SqlServerFacts>(new InvalidOperationException("server down")));
        var time = new RecordingTimeProvider();
        var settings = Settings("Server=tcp:127.0.0.1,1;Database=ControlHome_Test_retry;Encrypt=False");
        settings["Database:StartupTimeout"] = "00:00:02";
        await using var services = InfrastructureHost.Build(settings, s => s.AddSingleton(probe).AddSingleton<TimeProvider>(time));
        var readiness = services.GetRequiredService<DatabaseReadiness>();
        var initializer = services.GetRequiredService<DatabaseInitializer>();

        await initializer.StartAsync(ct);
        try
        {
            // Start-up: attempt, wait 1 s, attempt, give up (the next wait would pass the 2 s timeout).
            (await time.NextTimerAsync()).ShouldBe(TimeSpan.FromSeconds(1));
            readiness.State.ShouldBe(DatabaseState.Connecting);
            time.Advance(TimeSpan.FromSeconds(1));
            (await time.NextTimerAsync()).ShouldBe(TimeSpan.FromSeconds(60));
            readiness.State.ShouldBe(DatabaseState.Failed);
            readiness.LastError.ShouldBe("db.connection_failed");
            await probe.Received(2).ProbeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

            // Nothing happens before the minute is up ...
            time.Advance(TimeSpan.FromSeconds(59));
            await Task.Delay(200, ct);
            await probe.Received(2).ProbeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

            // ... then one attempt per minute, and the state stays Failed (not back to Connecting) while it fails.
            time.Advance(TimeSpan.FromSeconds(1));
            (await time.NextTimerAsync()).ShouldBe(TimeSpan.FromSeconds(60));
            await probe.Received(3).ProbeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            readiness.State.ShouldBe(DatabaseState.Failed);
            readiness.LastError.ShouldBe("db.connection_failed");

            // A retry runs the whole initialization: the server now answers, but it is too old.
            serverAnswers = true;
            time.Advance(TimeSpan.FromSeconds(60));
            (await time.NextTimerAsync()).ShouldBe(TimeSpan.FromSeconds(60));
            readiness.State.ShouldBe(DatabaseState.Failed);
            readiness.LastError.ShouldBe("sql.version_unsupported");
        }
        finally
        {
            await initializer.StopAsync(ct);
        }

        initializer.ExecuteTask!.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task Saved_connection_is_initialized_after_waiting_in_NotConfigured()
    {
        var ct = TestContext.Current.CancellationToken;
        await sql.EnsureStartedAsync();
        var suffix = NewSuffix();
        var connections = new FakeConnectionProvider();
        await using var services = InfrastructureHost.Build(
            new Dictionary<string, string?>(),
            s => s.AddSingleton<IDatabaseConnectionProvider>(connections));
        var readiness = services.GetRequiredService<DatabaseReadiness>();
        var initializer = services.GetRequiredService<DatabaseInitializer>();
        try
        {
            await initializer.StartAsync(ct);
            try
            {
                readiness.State.ShouldBe(DatabaseState.NotConfigured);

                connections.Set(sql.CreateDatabaseConnectionString(suffix));

                await WaitForStateAsync(readiness, DatabaseState.Ready, _readyTimeout, ct);
            }
            finally
            {
                await initializer.StopAsync(ct);
            }

            await using var scope = services.CreateAsyncScope();
            (await scope.ServiceProvider.GetRequiredService<ControlHomeDbContext>().Roles.CountAsync(ct)).ShouldBe(Roles.All.Count);
        }
        finally
        {
            await sql.DropDatabaseAsync(sql.DatabaseName(suffix));
        }
    }

    [Fact]
    public async Task Database_that_answers_only_after_the_startup_timeout_recovers_without_a_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        await sql.EnsureStartedAsync();
        var suffix = NewSuffix();
        var probe = new SwitchableProbe(new SqlServerProbe()) { Down = true };
        var time = new RecordingTimeProvider();
        var settings = Settings(sql.CreateDatabaseConnectionString(suffix));
        settings["Database:StartupTimeout"] = "00:00:02";
        await using var services = InfrastructureHost.Build(
            settings,
            s => s.AddSingleton<ISqlServerProbe>(probe).AddSingleton<TimeProvider>(time));
        var readiness = services.GetRequiredService<DatabaseReadiness>();
        var initializer = services.GetRequiredService<DatabaseInitializer>();
        try
        {
            await initializer.StartAsync(ct);
            try
            {
                (await time.NextTimerAsync()).ShouldBe(TimeSpan.FromSeconds(1));
                time.Advance(TimeSpan.FromSeconds(1));
                (await time.NextTimerAsync()).ShouldBe(TimeSpan.FromSeconds(60));
                readiness.State.ShouldBe(DatabaseState.Failed);

                probe.Down = false;
                time.Advance(TimeSpan.FromSeconds(60));

                await WaitForStateAsync(readiness, DatabaseState.Ready, _readyTimeout, ct);
                readiness.LastError.ShouldBeNull();
            }
            finally
            {
                await initializer.StopAsync(ct);
            }
        }
        finally
        {
            await sql.DropDatabaseAsync(sql.DatabaseName(suffix));
        }
    }

    private static Dictionary<string, string?> Settings(string connectionString) => new()
    {
        ["ConnectionStrings:ControlHome"] = connectionString,
    };

    private static string NewSuffix() => RandomNumberGenerator.GetHexString(8, lowercase: true);

    /// <summary>Starts the initializer and waits until it settles (Ready or Failed), then stops it.</summary>
    private static async Task<DatabaseReadiness> RunInitializerAsync(ServiceProvider services, CancellationToken ct)
    {
        var readiness = services.GetRequiredService<DatabaseReadiness>();
        var initializer = services.GetRequiredService<DatabaseInitializer>();
        await initializer.StartAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_readyTimeout);
            while (readiness.State is not (DatabaseState.Ready or DatabaseState.Failed))
            {
                await Task.Delay(50, timeout.Token);
            }
        }
        finally
        {
            await initializer.StopAsync(ct);
        }

        return readiness;
    }

    private static async Task WaitForStateAsync(DatabaseReadiness readiness, DatabaseState state, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);
        while (readiness.State != state)
        {
            await Task.Delay(50, timeoutSource.Token);
        }
    }

    private async Task<(int Compatibility, string Recovery)> DatabaseSettingsAsync(string name, CancellationToken ct)
    {
        await using var connection = new SqlConnection(sql.MasterConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            "SELECT compatibility_level, recovery_model_desc FROM sys.databases WHERE name = @name",
            connection);
        command.Parameters.AddWithValue("@name", name);
        await using var reader = await command.ExecuteReaderAsync(ct);
        (await reader.ReadAsync(ct)).ShouldBeTrue();
        return (Convert.ToInt32(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture), reader.GetString(1));
    }

    private async Task<List<string>> TablesAsync(string database, CancellationToken ct)
    {
        await using var connection = new SqlConnection(
            new SqlConnectionStringBuilder(sql.MasterConnectionString) { InitialCatalog = database }.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            "SELECT TABLE_SCHEMA + '.' + TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'",
            connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var tables = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private async Task<string?> ColumnTypeAsync(string database, string table, string column, CancellationToken ct)
    {
        await using var connection = new SqlConnection(
            new SqlConnectionStringBuilder(sql.MasterConnectionString) { InitialCatalog = database }.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            "SELECT TYPE_NAME(user_type_id) FROM sys.columns WHERE object_id = OBJECT_ID(@table) AND name = @column",
            connection);
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", column);
        return (string?)await command.ExecuteScalarAsync(ct);
    }
}

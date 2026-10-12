using System.Security.Cryptography;
using ControlHome.Domain.Security;
using ControlHome.Infrastructure.Persistence;
using ControlHome.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
                ],
                ignoreOrder: true);
            (await ColumnTypeAsync(name, "ch.Homes", "Tariff", ct)).ShouldBe("json");
            (await ColumnTypeAsync(name, "ch.AuditLog", "DetailsJson", ct)).ShouldBe("json");
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
    public async Task Missing_database_with_a_disallowed_name_is_not_created()
    {
        // Only ever against the throwaway container: the point is that the guard refuses, so never risk a shared server.
        Assert.SkipWhen(sql.UsesSharedServer, "Runs only against the Testcontainers SQL Server.");
        var ct = TestContext.Current.CancellationToken;
        await sql.EnsureStartedAsync();
        const string DisallowedName = "ControlHome_NotAllowed";
        var connectionString = new SqlConnectionStringBuilder(sql.MasterConnectionString) { InitialCatalog = DisallowedName }.ConnectionString;
        await using var services = InfrastructureHost.Build(Settings(connectionString));

        var readiness = await RunInitializerAsync(services, ct);

        readiness.State.ShouldBe(DatabaseState.Failed);
        readiness.LastError.ShouldBe("db.create_denied");
        await using var connection = new SqlConnection(sql.MasterConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand("SELECT DB_ID(@name)", connection);
        command.Parameters.AddWithValue("@name", DisallowedName);
        (await command.ExecuteScalarAsync(ct)).ShouldBe(DBNull.Value);
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
    public async Task Without_a_connection_string_the_state_is_NotConfigured()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = InfrastructureHost.Build(new Dictionary<string, string?>());

        var readiness = await RunInitializerAsync(services, ct);

        readiness.State.ShouldBe(DatabaseState.NotConfigured);
    }

    private static Dictionary<string, string?> Settings(string connectionString) => new()
    {
        ["ConnectionStrings:ControlHome"] = connectionString,
    };

    private static string NewSuffix() => RandomNumberGenerator.GetHexString(8, lowercase: true);

    /// <summary>Starts the initializer and waits until it settles (Ready, Failed or NotConfigured).</summary>
    private static async Task<DatabaseReadiness> RunInitializerAsync(ServiceProvider services, CancellationToken ct)
    {
        var readiness = services.GetRequiredService<DatabaseReadiness>();
        var initializer = services.GetRequiredService<DatabaseInitializer>();
        await initializer.StartAsync(ct);
        try
        {
            await initializer.ExecuteTask!.WaitAsync(_readyTimeout, ct);
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

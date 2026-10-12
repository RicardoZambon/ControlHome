using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ControlHome.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace ControlHome.TestSupport;

/// <summary>
/// One SQL Server per test assembly (register with <c>[assembly: AssemblyFixture(typeof(SqlServerFixture))]</c>).
/// <list type="bullet">
/// <item>If <c>CONTROLHOME_TEST_SQLSERVER</c> is set (a server-level connection string), databases are created on that
/// server as <c>ControlHome_Test_&lt;8 hex run id&gt;&lt;suffix&gt;</c>; <c>ControlHome_Test_%</c> databases older than 2 h are
/// dropped first, and this run's databases are dropped when it ends.</item>
/// <item>Otherwise a Testcontainers SQL Server 2025 Express container is started (and removed afterwards).</item>
/// </list>
/// Every CREATE/DROP goes through <see cref="DatabaseNameGuard"/>: no other database is ever touched.
/// The server is started lazily, by the first <see cref="EnsureStartedAsync"/>, so unit tests in the same assembly never
/// need Docker.
/// </summary>
public sealed partial class SqlServerFixture : IAsyncLifetime
{
    public const string ServerEnvironmentVariable = "CONTROLHOME_TEST_SQLSERVER";
    public const string ContainerImage = "mcr.microsoft.com/mssql/server:2025-CU9-ubuntu-24.04";

    private static readonly TimeSpan _staleAfter = TimeSpan.FromHours(2);

    private readonly ConcurrentDictionary<string, byte> _databases = new(StringComparer.Ordinal);
    private readonly string? _sharedServer = Environment.GetEnvironmentVariable(ServerEnvironmentVariable);
    private readonly Lazy<Task> _start;
    private MsSqlContainer? _container;
    private string? _serverConnectionString;

    public SqlServerFixture() => _start = new Lazy<Task>(StartAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Random per test run; part of every database name this fixture hands out.</summary>
    public string RunId { get; } = RandomNumberGenerator.GetHexString(8, lowercase: true);

    /// <summary>True when using <c>CONTROLHOME_TEST_SQLSERVER</c> instead of a container.</summary>
    public bool UsesSharedServer => !string.IsNullOrWhiteSpace(_sharedServer);

    /// <summary>Connection string to the server's <c>master</c> database (for assertions on <c>sys.databases</c>).</summary>
    public string MasterConnectionString =>
        new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = "master" }.ConnectionString;

    private string ServerConnectionString =>
        _serverConnectionString ?? throw new InvalidOperationException($"Call {nameof(EnsureStartedAsync)} first.");

    /// <summary>Nothing to do up front: the server starts on first use.</summary>
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>Starts the container (or connects to the shared server and removes stale test databases) once.</summary>
    public Task EnsureStartedAsync() => _start.Value;

    private async Task StartAsync()
    {
        if (UsesSharedServer)
        {
            _serverConnectionString = WithoutDatabase(_sharedServer!);
            await DropStaleDatabasesAsync();
            return;
        }

        _container = new MsSqlBuilder(ContainerImage).WithEnvironment("MSSQL_PID", "Express").Build();
        await _container.StartAsync();
        _serverConnectionString = WithoutDatabase(_container.GetConnectionString());
    }

    /// <summary>
    /// Connection string for a new database <c>ControlHome_Test_&lt;run id&gt;&lt;suffix&gt;</c> (letters and digits only).
    /// The database is not created here — the app's <c>DatabaseInitializer</c> creates it — but it is dropped later.
    /// Requires <see cref="EnsureStartedAsync"/>.
    /// </summary>
    public string CreateDatabaseConnectionString(string suffix)
    {
        var name = DatabaseName(suffix);
        _databases.TryAdd(name, 0);
        return new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = name }.ConnectionString;
    }

    /// <summary>The database name <see cref="CreateDatabaseConnectionString"/> uses for <paramref name="suffix"/>.</summary>
    public string DatabaseName(string suffix)
    {
        ArgumentException.ThrowIfNullOrEmpty(suffix);
        if (!AlphaNumeric().IsMatch(suffix))
        {
            throw new ArgumentException("Use letters and digits only.", nameof(suffix));
        }

        var name = $"ControlHome_Test_{RunId}{suffix}";
        DatabaseNameGuard.EnsureAllowed(name);
        return name;
    }

    /// <summary>
    /// Creates an empty database <c>ControlHome_Test_&lt;run id&gt;&lt;suffix&gt;</c> (as if a DBA had created it) and returns its
    /// connection string; <paramref name="compatibilityLevel"/> sets its compatibility level. Dropped later like the others.
    /// </summary>
    public async Task<string> CreateEmptyDatabaseAsync(string suffix, int? compatibilityLevel = null)
    {
        await EnsureStartedAsync();
        var connectionString = CreateDatabaseConnectionString(suffix);
        var name = DatabaseName(suffix);
        DatabaseNameGuard.EnsureAllowed(name);

        await using var connection = new SqlConnection(MasterConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@name) + N';';
            IF @level IS NOT NULL
                SET @sql += N' ALTER DATABASE ' + QUOTENAME(@name) + N' SET COMPATIBILITY_LEVEL = ' + CAST(@level AS nvarchar(10)) + N';';
            EXEC (@sql);
            """;
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@level", (object?)compatibilityLevel ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
        return connectionString;
    }

    /// <summary>Drops a database this fixture handed out (no-op if it was never created).</summary>
    public async Task DropDatabaseAsync(string databaseName)
    {
        DatabaseNameGuard.EnsureAllowed(databaseName);
        if (!_databases.TryRemove(databaseName, out _))
        {
            throw new InvalidOperationException($"Database {databaseName} was not created by this fixture.");
        }

        await DropIfExistsAsync(databaseName);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            return;
        }

        if (_serverConnectionString is not null)
        {
            foreach (var name in _databases.Keys.ToArray())
            {
                await DropIfExistsAsync(name);
            }
        }
    }

    private async Task DropStaleDatabasesAsync()
    {
        var stale = new List<string>();
        await using (var connection = new SqlConnection(MasterConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT name FROM sys.databases
                WHERE name LIKE N'ControlHome[_]Test[_]%' AND create_date < DATEADD(SECOND, -@age, GETDATE());
                """;
            command.Parameters.AddWithValue("@age", (int)_staleAfter.TotalSeconds);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                stale.Add(reader.GetString(0));
            }
        }

        foreach (var name in stale.Where(n => DatabaseNameGuard.IsAllowed(n)))
        {
            await DropIfExistsAsync(name);
        }
    }

    private async Task DropIfExistsAsync(string databaseName)
    {
        DatabaseNameGuard.EnsureAllowed(databaseName);

        // Close this process's pooled connections to it first; ROLLBACK IMMEDIATE ends any other session.
        SqlConnection.ClearPool(new SqlConnection(
            new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = databaseName }.ConnectionString));

        await using var connection = new SqlConnection(MasterConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF DB_ID(@name) IS NOT NULL
            BEGIN
                DECLARE @sql nvarchar(max) =
                    N'ALTER DATABASE ' + QUOTENAME(@name) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; ' +
                    N'DROP DATABASE ' + QUOTENAME(@name) + N';';
                EXEC (@sql);
            END
            """;
        command.Parameters.AddWithValue("@name", databaseName);
        await command.ExecuteNonQueryAsync();
    }

    private static string WithoutDatabase(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        builder.Remove("Initial Catalog");
        return builder.ConnectionString;
    }

    [GeneratedRegex("^[A-Za-z0-9]+$")]
    private static partial Regex AlphaNumeric();
}

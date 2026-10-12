using ControlHome.Application.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlHome.Infrastructure.Persistence;

/// <summary>
/// Brings the database to <see cref="DatabaseState.Ready"/> in the background, so the app starts (and answers 503
/// <c>db.unavailable</c>) even when SQL Server is down: retries the connection with back-off up to
/// <see cref="DatabaseOptions.StartupTimeout"/>, requires SQL Server 2025 (major version 17), creates the database if
/// missing (RECOVERY SIMPLE, compatibility level 170), applies migrations and seeds the roles.
/// </summary>
public sealed partial class DatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    IDatabaseConnectionProvider connections,
    DatabaseReadiness readiness,
    IOptions<DatabaseOptions> options,
    TimeProvider timeProvider,
    ILogger<DatabaseInitializer> logger) : BackgroundService
{
    public const int RequiredMajorVersion = 17;
    public const int RequiredCompatibilityLevel = 170;

    private static readonly TimeSpan _initialRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _maxRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly string[] _systemDatabases = ["master", "model", "msdb", "tempdb"];

    /// <summary>Publishes the initial state synchronously (ExecuteAsync runs on a background task since .NET 10).</summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        readiness.SetState(connections.GetConnectionString() is null ? DatabaseState.NotConfigured : DatabaseState.Connecting);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = connections.GetConnectionString();
        if (connectionString is null)
        {
            readiness.SetState(DatabaseState.NotConfigured);
            LogNotConfigured();
            return;
        }

        readiness.SetState(DatabaseState.Connecting);
        try
        {
            await InitializeAsync(connectionString, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutting down.
        }
    }

    private async Task InitializeAsync(string connectionString, CancellationToken cancellationToken)
    {
        string databaseName;
        try
        {
            databaseName = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        }
        catch (ArgumentException)
        {
            // Never include the parser message: it may quote part of the (secret) connection string.
            Fail(ErrorCodes.DbConnectionFailed, "the connection string could not be parsed");
            return;
        }

        if (string.IsNullOrWhiteSpace(databaseName) || _systemDatabases.Contains(databaseName, StringComparer.OrdinalIgnoreCase))
        {
            Fail(ErrorCodes.DbConnectionFailed, "the connection string must name the application database (Database=...)");
            return;
        }

        var server = await ProbeWithRetryAsync(connectionString, databaseName, cancellationToken);
        if (server is null)
        {
            return;
        }

        if (server.MajorVersion < RequiredMajorVersion)
        {
            LogVersionUnsupported(server.MajorVersion, RequiredMajorVersion);
            Fail(ErrorCodes.SqlVersionUnsupported, $"SQL Server 2025 (major version {RequiredMajorVersion}) or later is required");
            return;
        }

        readiness.SetState(DatabaseState.Migrating);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ControlHomeDbContext>();

            if (!server.DatabaseExists)
            {
                if (!await TryCreateDatabaseAsync(db, databaseName, cancellationToken))
                {
                    return;
                }
            }
            else if (server.CompatibilityLevel < RequiredCompatibilityLevel)
            {
                LogCompatibilityUnsupported(databaseName, server.CompatibilityLevel ?? 0, RequiredCompatibilityLevel);
                Fail(
                    ErrorCodes.SqlCompatibilityUnsupported,
                    $"run ALTER DATABASE [{databaseName}] SET COMPATIBILITY_LEVEL = {RequiredCompatibilityLevel}");
                return;
            }

            if (options.Value.AutoMigrate)
            {
                await db.Database.MigrateAsync(cancellationToken);
            }
            else if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
            {
                Fail(ErrorCodes.DbMigrationsPending, "migrations are pending and Database:AutoMigrate is false");
                return;
            }

            await RoleSeeder.SeedAsync(db, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogMigrationFailed(ex, databaseName);
            Fail(ErrorCodes.DbMigrationFailed, "the database could not be created or migrated");
            return;
        }

        readiness.SetState(DatabaseState.Ready);
        LogReady(databaseName);
    }

    private async Task<bool> TryCreateDatabaseAsync(ControlHomeDbContext db, string databaseName, CancellationToken cancellationToken)
    {
        // The app's own create-if-missing path: the only caller allowed to create the production name.
        if (!DatabaseNameGuard.IsAllowed(databaseName, allowProduction: true))
        {
            LogCreateRefused(databaseName);
            Fail(ErrorCodes.DbCreateDenied, $"database '{databaseName}' does not exist and that name may not be created by the app");
            return false;
        }

        try
        {
            await db.GetService<IRelationalDatabaseCreator>().CreateAsync(cancellationToken);
        }
        catch (SqlException ex) when (ex.Number == 262)
        {
            // 262: CREATE DATABASE permission denied in database 'master'.
            LogCreateDenied(databaseName);
            Fail(ErrorCodes.DbCreateDenied, $"the login may not create database '{databaseName}'");
            return false;
        }

        // A database the app created itself: simple recovery (no log backups on a home server) and SQL Server 2025 semantics.
        await db.Database.ExecuteSqlRawAsync("ALTER DATABASE CURRENT SET RECOVERY SIMPLE", cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            $"ALTER DATABASE CURRENT SET COMPATIBILITY_LEVEL = {RequiredCompatibilityLevel}",
            cancellationToken);
        LogDatabaseCreated(databaseName);
        return true;
    }

    private async Task<ServerInfo?> ProbeWithRetryAsync(string connectionString, string databaseName, CancellationToken cancellationToken)
    {
        var deadline = timeProvider.GetUtcNow() + options.Value.StartupTimeout;
        var delay = _initialRetryDelay;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ProbeAsync(connectionString, databaseName, cancellationToken);
            }
            catch (SqlException ex)
            {
                LogConnectAttemptFailed(attempt, ex.Number, ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                LogConnectAttemptFailed(attempt, 0, ex.Message);
            }

            if (timeProvider.GetUtcNow() + delay >= deadline)
            {
                Fail(ErrorCodes.DbConnectionFailed, $"the database server did not answer within {options.Value.StartupTimeout}");
                return null;
            }

            await Task.Delay(delay, timeProvider, cancellationToken);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _maxRetryDelay.Ticks));
        }
    }

    /// <summary>Connects to <c>master</c> (the app database may not exist yet) and reads version and database facts.</summary>
    private static async Task<ServerInfo> ProbeAsync(string connectionString, string databaseName, CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", Pooling = false };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int),
                   DB_ID(@name),
                   (SELECT compatibility_level FROM sys.databases WHERE name = @name);
            """;
        command.Parameters.Add(new SqlParameter("@name", System.Data.SqlDbType.NVarChar, 128) { Value = databaseName });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var major = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
        var exists = !reader.IsDBNull(1);
        int? compatibility = reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture);
        return new ServerInfo(major, exists, compatibility);
    }

    private void Fail(string errorCode, string reason)
    {
        readiness.SetState(DatabaseState.Failed, errorCode);
        LogFailed(errorCode, reason);
    }

    private sealed record ServerInfo(int MajorVersion, bool DatabaseExists, int? CompatibilityLevel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No database connection string is configured (ConnectionStrings:ControlHome); the API answers 503 db.unavailable")]
    private partial void LogNotConfigured();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database connection attempt {Attempt} failed (SQL error {SqlErrorNumber}): {Reason}")]
    private partial void LogConnectAttemptFailed(int attempt, int sqlErrorNumber, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "SQL Server major version {MajorVersion} is not supported; {RequiredMajorVersion} (SQL Server 2025) or later is required")]
    private partial void LogVersionUnsupported(int majorVersion, int requiredMajorVersion);

    [LoggerMessage(Level = LogLevel.Error, Message = "Database {DatabaseName} has compatibility level {CompatibilityLevel}; {RequiredLevel} is required")]
    private partial void LogCompatibilityUnsupported(string databaseName, int compatibilityLevel, int requiredLevel);

    [LoggerMessage(Level = LogLevel.Error, Message = "Refusing to create database {DatabaseName}: the name is not allowed")]
    private partial void LogCreateRefused(string databaseName);

    [LoggerMessage(Level = LogLevel.Error, Message = "The SQL login may not create database {DatabaseName}; create it manually and restart")]
    private partial void LogCreateDenied(string databaseName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created database {DatabaseName} (recovery SIMPLE, compatibility level 170)")]
    private partial void LogDatabaseCreated(string databaseName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Creating or migrating database {DatabaseName} failed")]
    private partial void LogMigrationFailed(Exception exception, string databaseName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Database initialization failed ({ErrorCode}): {Reason}")]
    private partial void LogFailed(string errorCode, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database {DatabaseName} is ready")]
    private partial void LogReady(string databaseName);
}

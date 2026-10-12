using ControlHome.Application.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace ControlHome.Infrastructure.Persistence;

/// <summary>
/// Brings the database to <see cref="DatabaseState.Ready"/> in the background, so the app starts (and answers 503
/// <c>db.unavailable</c>) even when SQL Server is down or not configured yet:
/// <list type="bullet">
/// <item>without a connection string it waits in <see cref="DatabaseState.NotConfigured"/> until one is saved
/// (<see cref="IDatabaseConnectionProvider.GetChangeToken"/>, pre-database setup mode);</item>
/// <item>retries the connection with back-off up to <see cref="DatabaseOptions.StartupTimeout"/>, then reports
/// <see cref="DatabaseState.Failed"/> and keeps retrying every <see cref="DatabaseOptions.RetryInterval"/> (the state stays
/// Failed until an attempt succeeds), so a database that comes up late recovers without a restart;</item>
/// <item>requires SQL Server 2025 (major version 17), creates the database if missing (RECOVERY SIMPLE, compatibility level
/// 170; any name the operator configured, never a DROP), applies migrations and seeds the roles.</item>
/// </list>
/// </summary>
public sealed partial class DatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    IDatabaseConnectionProvider connections,
    ISqlServerProbe probe,
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

    /// <summary>The error code last logged at Error level, so a failure repeated by every retry is logged once.</summary>
    private string? _reportedFailure;

    /// <summary>Publishes the initial state synchronously (ExecuteAsync runs on a background task since .NET 10).</summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        DatabaseState initial;
        try
        {
            initial = connections.GetConnectionString() is null ? DatabaseState.NotConfigured : DatabaseState.Connecting;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ExecuteAsync reads it again and reports the failure; starting the host must never fail because of it.
            initial = DatabaseState.Connecting;
        }

        readiness.SetState(initial);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await WaitUntilConfiguredAsync(stoppingToken);

            readiness.SetState(DatabaseState.Connecting);
            var deadline = timeProvider.GetUtcNow() + options.Value.StartupTimeout;
            if (await TryInitializeAsync(deadline, stoppingToken))
            {
                return;
            }

            var interval = options.Value.RetryInterval;
            LogRetrying(interval);
            do
            {
                await Task.Delay(interval, timeProvider, stoppingToken);
            }
            while (!await TryInitializeAsync(deadline: null, stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutting down.
        }
    }

    /// <summary>Returns once a connection string is configured (or reading it fails, which the first attempt then reports).</summary>
    private async Task WaitUntilConfiguredAsync(CancellationToken cancellationToken)
    {
        var logged = false;
        while (true)
        {
            // Take the token before reading, so a connection saved in between is never missed.
            var changed = connections.GetChangeToken();
            try
            {
                if (connections.GetConnectionString() is not null)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return;
            }

            readiness.SetState(DatabaseState.NotConfigured);
            if (!logged)
            {
                LogNotConfigured();
                logged = true;
            }

            await WaitForChangeAsync(changed, cancellationToken);
            LogConfigurationChanged();
        }
    }

    private static async Task WaitForChangeAsync(IChangeToken token, CancellationToken cancellationToken)
    {
        if (token.HasChanged)
        {
            return;
        }

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (token.RegisterChangeCallback(static state => ((TaskCompletionSource)state!).TrySetResult(), signal))
        {
            await signal.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// One initialization: connect (retrying with back-off until <paramref name="deadline"/>; a single attempt when null),
    /// check, create, migrate, seed. True when the database is ready; otherwise the state is <see cref="DatabaseState.Failed"/>.
    /// </summary>
    private async Task<bool> TryInitializeAsync(DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        try
        {
            var connectionString = connections.GetConnectionString();
            if (connectionString is null)
            {
                Fail(ErrorCodes.DbConnectionFailed, "no database connection is configured");
                return false;
            }

            return await InitializeAsync(connectionString, deadline, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Last line of defence: a faulted BackgroundService would stop the whole host, but the API must keep answering
            // (503 db.unavailable, /api/setup/status). Parser-style exceptions may quote the (secret) connection string,
            // so only their type is logged.
            if (!IsNewFailure(ErrorCodes.DbConnectionFailed))
            {
                LogUnexpectedFailureType(LogLevel.Debug, ex.GetType().FullName ?? ex.GetType().Name);
            }
            else if (ex is ArgumentException or FormatException)
            {
                LogUnexpectedFailureType(LogLevel.Error, ex.GetType().FullName ?? ex.GetType().Name);
            }
            else
            {
                LogUnexpectedFailure(ex);
            }

            Fail(ErrorCodes.DbConnectionFailed, "unexpected error while initializing the database");
            return false;
        }
    }

    private async Task<bool> InitializeAsync(string connectionString, DateTimeOffset? deadline, CancellationToken cancellationToken)
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
            return false;
        }

        if (string.IsNullOrWhiteSpace(databaseName) || _systemDatabases.Contains(databaseName, StringComparer.OrdinalIgnoreCase))
        {
            Fail(ErrorCodes.DbConnectionFailed, "the connection string must name the application database (Database=...)");
            return false;
        }

        var server = await ProbeAsync(connectionString, databaseName, deadline, cancellationToken);
        if (server is null)
        {
            return false;
        }

        if (server.MajorVersion < RequiredMajorVersion)
        {
            if (IsNewFailure(ErrorCodes.SqlVersionUnsupported))
            {
                LogVersionUnsupported(server.MajorVersion, RequiredMajorVersion);
            }

            Fail(ErrorCodes.SqlVersionUnsupported, $"SQL Server 2025 (major version {RequiredMajorVersion}) or later is required");
            return false;
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
                    return false;
                }
            }
            else if (server.CompatibilityLevel < RequiredCompatibilityLevel)
            {
                if (IsNewFailure(ErrorCodes.SqlCompatibilityUnsupported))
                {
                    LogCompatibilityUnsupported(databaseName, server.CompatibilityLevel ?? 0, RequiredCompatibilityLevel);
                }

                Fail(
                    ErrorCodes.SqlCompatibilityUnsupported,
                    $"run ALTER DATABASE [{databaseName}] SET COMPATIBILITY_LEVEL = {RequiredCompatibilityLevel}");
                return false;
            }

            if (options.Value.AutoMigrate)
            {
                await db.Database.MigrateAsync(cancellationToken);
            }
            else if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
            {
                Fail(ErrorCodes.DbMigrationsPending, "migrations are pending and Database:AutoMigrate is false");
                return false;
            }

            await RoleSeeder.SeedAsync(db, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (IsNewFailure(ErrorCodes.DbMigrationFailed))
            {
                LogMigrationFailed(ex, databaseName);
            }

            Fail(ErrorCodes.DbMigrationFailed, "the database could not be created or migrated");
            return false;
        }

        _reportedFailure = null;
        readiness.SetState(DatabaseState.Ready);
        LogReady(databaseName);
        return true;
    }

    /// <summary>
    /// The app's own create-if-missing path. It creates whatever database the operator configured (environment or setup
    /// wizard: that is the production database they chose); <see cref="DatabaseNameGuard"/> is for tooling and tests only.
    /// </summary>
    private async Task<bool> TryCreateDatabaseAsync(ControlHomeDbContext db, string databaseName, CancellationToken cancellationToken)
    {
        try
        {
            await db.GetService<IRelationalDatabaseCreator>().CreateAsync(cancellationToken);
        }
        catch (SqlException ex) when (ex.Number == 262)
        {
            // 262: CREATE DATABASE permission denied in database 'master'.
            if (IsNewFailure(ErrorCodes.DbCreateDenied))
            {
                LogCreateDenied(databaseName);
            }

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

    private async Task<SqlServerFacts?> ProbeAsync(
        string connectionString,
        string databaseName,
        DateTimeOffset? deadline,
        CancellationToken cancellationToken)
    {
        var delay = _initialRetryDelay;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await probe.ProbeAsync(connectionString, databaseName, cancellationToken);
            }
            catch (SqlException ex)
            {
                LogAttemptFailed(deadline is null, attempt, ex.Number, ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                LogAttemptFailed(deadline is null, attempt, 0, ex.Message);
            }

            if (deadline is null)
            {
                Fail(ErrorCodes.DbConnectionFailed, "the database server did not answer");
                return null;
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

    private void LogAttemptFailed(bool retrying, int attempt, int sqlErrorNumber, string reason)
    {
        if (retrying)
        {
            LogRetryAttemptFailed(sqlErrorNumber, reason);
        }
        else
        {
            LogConnectAttemptFailed(attempt, sqlErrorNumber, reason);
        }
    }

    /// <summary>Whether <paramref name="errorCode"/> differs from the failure already reported (retries repeat it quietly).</summary>
    private bool IsNewFailure(string errorCode) => !string.Equals(_reportedFailure, errorCode, StringComparison.Ordinal);

    private void Fail(string errorCode, string reason)
    {
        readiness.SetState(DatabaseState.Failed, errorCode);
        if (!IsNewFailure(errorCode))
        {
            LogStillFailing(errorCode, reason);
            return;
        }

        _reportedFailure = errorCode;
        LogFailed(errorCode, reason);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No database connection is configured (ConnectionStrings:ControlHome or the setup wizard); the API answers 503 db.unavailable until one is saved")]
    private partial void LogNotConfigured();

    [LoggerMessage(Level = LogLevel.Information, Message = "A database connection was saved; initializing the database")]
    private partial void LogConfigurationChanged();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database connection attempt {Attempt} failed (SQL error {SqlErrorNumber}): {Reason}")]
    private partial void LogConnectAttemptFailed(int attempt, int sqlErrorNumber, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Database retry failed (SQL error {SqlErrorNumber}): {Reason}")]
    private partial void LogRetryAttemptFailed(int sqlErrorNumber, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Retrying the database initialization every {RetryInterval} until it succeeds")]
    private partial void LogRetrying(TimeSpan retryInterval);

    [LoggerMessage(Level = LogLevel.Error, Message = "SQL Server major version {MajorVersion} is not supported; {RequiredMajorVersion} (SQL Server 2025) or later is required")]
    private partial void LogVersionUnsupported(int majorVersion, int requiredMajorVersion);

    [LoggerMessage(Level = LogLevel.Error, Message = "Database {DatabaseName} has compatibility level {CompatibilityLevel}; {RequiredLevel} is required")]
    private partial void LogCompatibilityUnsupported(string databaseName, int compatibilityLevel, int requiredLevel);

    [LoggerMessage(Level = LogLevel.Error, Message = "The SQL login may not create database {DatabaseName}; create it manually (ControlHome retries on its own)")]
    private partial void LogCreateDenied(string databaseName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created database {DatabaseName} (recovery SIMPLE, compatibility level 170)")]
    private partial void LogDatabaseCreated(string databaseName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Creating or migrating database {DatabaseName} failed")]
    private partial void LogMigrationFailed(Exception exception, string databaseName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Database initialization failed ({ErrorCode}): {Reason}")]
    private partial void LogFailed(string errorCode, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Database initialization still failing ({ErrorCode}): {Reason}")]
    private partial void LogStillFailing(string errorCode, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database {DatabaseName} is ready")]
    private partial void LogReady(string databaseName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error while initializing the database")]
    private partial void LogUnexpectedFailure(Exception exception);

    [LoggerMessage(Message = "Unexpected error while initializing the database ({ExceptionType}); check the connection string")]
    private partial void LogUnexpectedFailureType(LogLevel level, string exceptionType);
}

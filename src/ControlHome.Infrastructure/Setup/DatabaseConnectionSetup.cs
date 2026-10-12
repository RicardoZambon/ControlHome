using System.Globalization;
using System.Security.Authentication;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Common;
using ControlHome.Application.Setup;
using ControlHome.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace ControlHome.Infrastructure.Setup;

/// <summary>
/// <see cref="IDatabaseConnectionSetup"/> for SQL Server: builds the connection string from the wizard's fields (TLS always
/// on, 10 s connect timeout, application name <c>ControlHome</c>), probes <c>master</c> (<see cref="ISqlServerProbe"/>) and
/// saves it through <see cref="IDatabaseConnectionProvider.TrySave"/>, which wakes the <see cref="DatabaseInitializer"/>.
/// Failure messages are fixed texts (with the host, port and user name the operator typed): never the password, the
/// connection string or the server's own message.
/// </summary>
public sealed partial class DatabaseConnectionSetup(
    IDatabaseConnectionProvider connections,
    ISqlServerProbe probe,
    ILogger<DatabaseConnectionSetup> logger) : IDatabaseConnectionSetup
{
    public const string ApplicationName = "ControlHome";
    public const int ConnectTimeoutSeconds = 10;

    /// <summary>The least-privilege login suggested by the <c>db.create_denied</c> script.</summary>
    public const string AppLogin = "ControlHome_App";

    public const string PasswordPlaceholder = "<choose-a-strong-password>";

    /// <summary>The extension of <c>db.connection_failed</c> that says what went wrong (the SPA translates it).</summary>
    public const string ReasonExtension = "reason";

    /// <summary>The extension of <c>db.create_denied</c> with the T-SQL to run instead.</summary>
    public const string ScriptExtension = "script";

    private static readonly HashSet<int> _loginFailures = [18456, 18452, 18470, 18486, 18487, 18488];
    private static readonly HashSet<int> _unreachable = [-2, -1, 2, 40, 53, 258, 1225, 10053, 10054, 10060, 10061, 10065, 11001, 11004];

    public async Task<Result> ConfigureAsync(DatabaseSetupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!DatabaseSetupRequestValidator.IsValidDatabaseName(request.Database))
        {
            throw new ArgumentException("The database name was not validated.", nameof(request));
        }

        var connectionString = BuildConnectionString(request);
        SqlServerFacts facts;
        try
        {
            facts = await probe.ProbeAsync(connectionString, request.Database, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var sql = ex as SqlException;
            var failure = DescribeFailure(sql?.Number, ex.Message, IsTlsFailure(ex), request);
            LogConnectionFailed(request.Host, request.Port, failure.Reason, sql?.Number ?? 0, ex.GetType().Name);
            return ConnectionFailed(failure);
        }

        if (facts.MajorVersion < DatabaseInitializer.RequiredMajorVersion)
        {
            return AppError.Validation(
                ErrorCodes.SqlVersionUnsupported,
                $"The server runs SQL Server major version {facts.MajorVersion}; ControlHome needs SQL Server 2025 (version {DatabaseInitializer.RequiredMajorVersion}) or later.",
                new Dictionary<string, object?> { ["majorVersion"] = facts.MajorVersion });
        }

        if (facts.DatabaseExists)
        {
            if (!facts.HasDatabaseAccess)
            {
                return ConnectionFailed(new ConnectionFailure(
                    "no_database_access",
                    $"The login '{request.UserName}' may not open database '{request.Database}'. Map it to the database, or use a login that may."));
            }

            if (facts.CompatibilityLevel < DatabaseInitializer.RequiredCompatibilityLevel)
            {
                return AppError.Validation(
                    ErrorCodes.SqlCompatibilityUnsupported,
                    $"Database '{request.Database}' has compatibility level {facts.CompatibilityLevel}; run ALTER DATABASE [{request.Database}] SET COMPATIBILITY_LEVEL = {DatabaseInitializer.RequiredCompatibilityLevel}.");
            }
        }
        else if (!facts.CanCreateDatabase)
        {
            LogCreateDenied(request.Host, request.Port, request.Database);
            return AppError.Conflict(
                ErrorCodes.DbCreateDenied,
                $"Database '{request.Database}' does not exist and the login may not create it. Run the script as an administrator, then try again.",
                new Dictionary<string, object?> { [ScriptExtension] = CreateDatabaseScript(request.Database) });
        }

        if (!connections.TrySave(connectionString))
        {
            return DatabaseSetupService.AlreadyConfigured();
        }

        LogSaved(request.Host, request.Port, request.Database, facts.DatabaseExists);
        return Result.Success();
    }

    /// <summary>The connection string for the wizard's fields.</summary>
    public static string BuildConnectionString(DatabaseSetupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = string.Create(CultureInfo.InvariantCulture, $"{request.Host.Trim()},{request.Port}"),
            InitialCatalog = request.Database,
            UserID = request.UserName,
            Password = request.Password,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = request.TrustServerCertificate,
            ConnectTimeout = ConnectTimeoutSeconds,
            ApplicationName = ApplicationName,
        };
        return builder.ConnectionString;
    }

    /// <summary>
    /// T-SQL an administrator runs when the login may not create the database: the database (compatibility level 170, simple
    /// recovery) and a least-privilege login <see cref="AppLogin"/> that owns only that database. Never drops anything.
    /// </summary>
    public static string CreateDatabaseScript(string database)
    {
        if (!DatabaseSetupRequestValidator.IsValidDatabaseName(database))
        {
            throw new ArgumentException("Not a valid database name.", nameof(database));
        }

        return $"""
            -- Run as a SQL Server administrator (for example sa, in SQL Server Management Studio or sqlcmd).
            -- Then sign in to the setup wizard with the login {AppLogin} and the password you chose here.
            USE [master];
            GO
            CREATE DATABASE [{database}];
            GO
            ALTER DATABASE [{database}] SET COMPATIBILITY_LEVEL = {DatabaseInitializer.RequiredCompatibilityLevel};
            ALTER DATABASE [{database}] SET RECOVERY SIMPLE;
            GO
            CREATE LOGIN [{AppLogin}] WITH PASSWORD = N'{PasswordPlaceholder}', CHECK_POLICY = ON;
            GO
            USE [{database}];
            GO
            CREATE USER [{AppLogin}] FOR LOGIN [{AppLogin}];
            ALTER ROLE [db_owner] ADD MEMBER [{AppLogin}];
            GO

            """;
    }

    /// <summary>
    /// A sanitized description of a failed connection: a <see cref="ConnectionFailure.Reason"/> the SPA translates, and a
    /// fixed English message that only repeats what the operator typed (host, port, user name), never the server's text.
    /// </summary>
    internal static ConnectionFailure DescribeFailure(int? sqlErrorNumber, string message, bool tlsFailure, DatabaseSetupRequest request)
    {
        var endpoint = string.Create(CultureInfo.InvariantCulture, $"{request.Host}:{request.Port}");
        if (sqlErrorNumber is { } number && _loginFailures.Contains(number))
        {
            return new ConnectionFailure(
                "login_failed",
                $"SQL Server at {endpoint} refused the login '{request.UserName}': check the user name and password, and that SQL Server authentication is enabled.");
        }

        if (tlsFailure || message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
        {
            return new ConnectionFailure(
                "certificate",
                $"The encrypted connection to {endpoint} failed: the server's certificate is not trusted. Turn on 'Trust server certificate', or give SQL Server a certificate this network trusts.");
        }

        if ((sqlErrorNumber is { } code && _unreachable.Contains(code))
            || message.Contains("network-related", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || message.Contains("server was not found", StringComparison.OrdinalIgnoreCase))
        {
            return new ConnectionFailure(
                "unreachable",
                $"SQL Server did not answer at {endpoint}: check the host and port, and that the server accepts TCP connections from the ControlHome server.");
        }

        return new ConnectionFailure(
            "other",
            sqlErrorNumber is { } other && other != 0
                ? string.Create(CultureInfo.InvariantCulture, $"SQL Server at {endpoint} refused the connection (error {other}).")
                : $"The connection to SQL Server at {endpoint} failed.");
    }

    private static bool IsTlsFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException)
            {
                return true;
            }
        }

        return false;
    }

    private static AppError ConnectionFailed(ConnectionFailure failure) =>
        AppError.Validation(
            ErrorCodes.DbConnectionFailed,
            failure.Message,
            new Dictionary<string, object?> { [ReasonExtension] = failure.Reason });

    /// <summary>
    /// <paramref name="Reason"/>: <c>login_failed</c>, <c>certificate</c>, <c>unreachable</c>, <c>no_database_access</c> or <c>other</c>.
    /// </summary>
    internal sealed record ConnectionFailure(string Reason, string Message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Setup wizard: connecting to SQL Server at {Host}:{Port} failed ({Reason}, SQL error {SqlErrorNumber}, {ExceptionType})")]
    private partial void LogConnectionFailed(string host, int port, string reason, int sqlErrorNumber, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Setup wizard: database {Database} does not exist on {Host}:{Port} and the login may not create it; the wizard shows the T-SQL to run")]
    private partial void LogCreateDenied(string host, int port, string database);

    [LoggerMessage(Level = LogLevel.Information, Message = "Setup wizard: saved the connection to database {Database} on {Host}:{Port} (database exists: {DatabaseExists})")]
    private partial void LogSaved(string host, int port, string database, bool databaseExists);
}

using System.Security.Cryptography;
using ControlHome.Application.Setup;
using ControlHome.TestSupport;
using Microsoft.Data.SqlClient;

namespace ControlHome.Api.IntegrationTests;

/// <summary>The API with no connection string at all (database state NotConfigured).</summary>
public sealed class NoDatabaseApiFactory(SqlServerFixture sql) : ControlHomeApiFactory(sql)
{
    protected override bool UseDatabase => false;
}

/// <summary>
/// Pre-database setup mode: no connection string and an empty data folder, rate limits raised (every request to
/// <c>POST /api/setup/database</c> counts). <see cref="NewDatabaseRequestAsync"/> hands out the test server's details for a
/// fresh <c>ControlHome_Test_*</c> database, which is dropped when the factory is disposed.
/// </summary>
public sealed class PreDatabaseApiFactory(SqlServerFixture sql) : ControlHomeApiFactory(sql)
{
    private readonly List<string> _databases = [];

    protected override bool UseDatabase => false;

    protected override void ConfigureSettings(IDictionary<string, string?> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings["RateLimiting:Setup:PermitLimit"] = "100000";
        settings["RateLimiting:Login:PermitLimit"] = "100000";
    }

    /// <summary>
    /// A valid request for the test SQL Server (container or <c>CONTROLHOME_TEST_SQLSERVER</c>) and a new database name;
    /// <c>null</c> when that server cannot be described by host, port and SQL login (named instance, Windows login).
    /// </summary>
    public async Task<DatabaseSetupRequest?> NewDatabaseRequestAsync()
    {
        await Sql.EnsureStartedAsync();
        var suffix = RandomNumberGenerator.GetHexString(8, lowercase: true);
        var server = new SqlConnectionStringBuilder(Sql.CreateDatabaseConnectionString(suffix));
        _databases.Add(Sql.DatabaseName(suffix));

        var dataSource = server.DataSource.Trim();
        if (dataSource.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            dataSource = dataSource["tcp:".Length..];
        }

        if (server.IntegratedSecurity || dataSource.Contains('\\', StringComparison.Ordinal) || string.IsNullOrEmpty(server.Password))
        {
            return null;
        }

        var parts = dataSource.Split(',', 2, StringSplitOptions.TrimEntries);
        var port = parts.Length == 2 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 1433;
        return new DatabaseSetupRequest(
            SetupCode,
            parts[0],
            port,
            Sql.DatabaseName(suffix),
            server.UserID,
            server.Password,
            TrustServerCertificate: true);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        foreach (var name in _databases)
        {
            await Sql.DropDatabaseAsync(name);
        }

        _databases.Clear();
    }
}

/// <summary>The API pointed at a closed port, giving up after two seconds (Connecting, then Failed).</summary>
public sealed class UnreachableDatabaseApiFactory(SqlServerFixture sql) : ControlHomeApiFactory(sql)
{
    // Port 1 on loopback: refused immediately. No credentials needed, the TCP connect never succeeds.
    public const string UnreachableConnectionString =
        "Server=tcp:127.0.0.1,1;Database=ControlHome_Test_unreachable;Connect Timeout=1;Encrypt=False";

    protected override bool UseDatabase => false;

    protected override void ConfigureSettings(IDictionary<string, string?> settings)
    {
        settings["ConnectionStrings:ControlHome"] = UnreachableConnectionString;
        settings["Database:StartupTimeout"] = "00:00:02";
    }
}

/// <summary>The API with a database in the Production environment (no test endpoints, no exception details).</summary>
public sealed class ProductionApiFactory(SqlServerFixture sql) : ControlHomeApiFactory(sql)
{
    protected override string EnvironmentName => "Production";
}

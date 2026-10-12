using ControlHome.TestSupport;

namespace ControlHome.Api.IntegrationTests;

/// <summary>The API with no connection string at all (database state NotConfigured).</summary>
public sealed class NoDatabaseApiFactory(SqlServerFixture sql) : ControlHomeApiFactory(sql)
{
    protected override bool UseDatabase => false;
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

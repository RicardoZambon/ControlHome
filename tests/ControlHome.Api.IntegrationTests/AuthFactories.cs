using ControlHome.TestSupport;

namespace ControlHome.Api.IntegrationTests;

/// <summary>
/// The API with rate limits raised far above what one test class does, for the setup / auth / user tests (they sign in
/// and out many times from the same client address). Each test class resets the app before every test.
/// </summary>
public class AuthApiFactory(SqlServerFixture sql) : ControlHomeApiFactory(sql)
{
    protected override void ConfigureSettings(IDictionary<string, string?> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings["RateLimiting:Setup:PermitLimit"] = "100000";
        settings["RateLimiting:Login:PermitLimit"] = "100000";
    }
}

/// <summary>
/// The setup / auth / user / home / audit test classes share one <see cref="AuthApiFactory"/> (one database) and run one
/// after the other: each test resets the app first, and a database per class would overload the shared SQL Server Express
/// while all fixtures start in parallel. Tests that need different host settings derive a second host over the same
/// database with <c>WithWebHostBuilder</c>.
/// </summary>
[CollectionDefinition(Name)]
public sealed class AuthCollection : ICollectionFixture<AuthApiFactory>
{
    public const string Name = "Auth";
}

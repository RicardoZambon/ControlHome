using Microsoft.Extensions.Configuration;

namespace ControlHome.Infrastructure.Persistence;

/// <summary>Where the database connection string comes from. Never log the returned value.</summary>
public interface IDatabaseConnectionProvider
{
    /// <summary>The connection string, or <c>null</c> when none is configured (pre-DB setup mode, milestone 0.4).</summary>
    string? GetConnectionString();
}

/// <summary>Reads <c>ConnectionStrings:ControlHome</c> (env <c>ConnectionStrings__ControlHome</c>, user-secrets in Development).</summary>
public sealed class ConfigurationDatabaseConnectionProvider(IConfiguration configuration) : IDatabaseConnectionProvider
{
    public const string ConnectionStringName = "ControlHome";

    public string? GetConnectionString()
    {
        var value = configuration.GetConnectionString(ConnectionStringName);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

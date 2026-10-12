using System.Globalization;
using Microsoft.Data.SqlClient;

namespace ControlHome.Infrastructure.Persistence;

/// <summary>What a connection to <c>master</c> tells about the server and the application database.</summary>
/// <param name="MajorVersion">17 is SQL Server 2025.</param>
/// <param name="CompatibilityLevel">The application database's compatibility level (when it exists).</param>
/// <param name="CanCreateDatabase">Whether the login holds <c>CREATE ANY DATABASE</c>.</param>
/// <param name="HasDatabaseAccess">Whether the login may open the application database (false when it does not exist).</param>
public sealed record SqlServerFacts(
    int MajorVersion,
    bool DatabaseExists,
    int? CompatibilityLevel,
    bool CanCreateDatabase,
    bool HasDatabaseAccess);

/// <summary>Connects to the server's <c>master</c> database (the application database may not exist yet).</summary>
public interface ISqlServerProbe
{
    /// <summary>
    /// Opens a connection with the server and credentials of <paramref name="connectionString"/> (database replaced by
    /// <c>master</c>, no pooling) and reads <see cref="SqlServerFacts"/> about <paramref name="databaseName"/>. Throws
    /// (<see cref="SqlException"/>, <see cref="InvalidOperationException"/>) when the server cannot be reached or refuses the login.
    /// </summary>
    Task<SqlServerFacts> ProbeAsync(string connectionString, string databaseName, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class SqlServerProbe : ISqlServerProbe
{
    public async Task<SqlServerFacts> ProbeAsync(string connectionString, string databaseName, CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", Pooling = false };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int),
                   DB_ID(@name),
                   (SELECT compatibility_level FROM sys.databases WHERE name = @name),
                   HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE ANY DATABASE'),
                   HAS_DBACCESS(@name);
            """;
        command.Parameters.Add(new SqlParameter("@name", System.Data.SqlDbType.NVarChar, 128) { Value = databaseName });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new SqlServerFacts(
            MajorVersion: reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            DatabaseExists: !reader.IsDBNull(1),
            CompatibilityLevel: reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
            CanCreateDatabase: !reader.IsDBNull(3) && Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture) == 1,
            HasDatabaseAccess: !reader.IsDBNull(4) && Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture) == 1);
    }
}

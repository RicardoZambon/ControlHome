using ControlHome.Application.Common;
using ControlHome.Application.Setup;

namespace ControlHome.Application.Abstractions;

/// <summary>Tests and saves the SQL Server connection entered in the pre-database setup wizard (implemented by Infrastructure).</summary>
public interface IDatabaseConnectionSetup
{
    /// <summary>
    /// Connects to the server with the request's fields (TLS always on) and checks it: reachable with these credentials, SQL
    /// Server 2025 or later, and the database either exists (and the login may open it) or may be created by the login. Then
    /// saves the connection (encrypted) and signals the database initializer to start. The request is validated first.
    /// </summary>
    /// <returns>
    /// Success, or 400 <c>db.connection_failed</c> (extension <c>reason</c>; never the server's own text),
    /// <c>sql.version_unsupported</c>, <c>sql.compatibility_unsupported</c>; 409 <c>db.create_denied</c> (extension
    /// <c>script</c>: the T-SQL an administrator can run instead) or <c>db.already_configured</c>.
    /// </returns>
    Task<Result> ConfigureAsync(DatabaseSetupRequest request, CancellationToken cancellationToken);
}

using System.Data;
using ControlHome.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace ControlHome.Infrastructure.Devices;

/// <summary>The result of <see cref="RuntimeLease.CheckAsync"/>.</summary>
public enum LeaseState
{
    /// <summary>Still held (possibly re-acquired after a reconnect).</summary>
    Held,

    /// <summary>Another process holds it now: stop the runtime.</summary>
    Lost,

    /// <summary>The database cannot be reached: keep running (nobody else can take the lease either) and check again.</summary>
    Unreachable,
}

/// <summary>
/// The single-active-runtime lease: <c>sp_getapplock @Resource = 'ControlHome.Runtime', @LockMode = 'Exclusive',
/// @LockOwner = 'Session'</c> held on a dedicated, unpooled connection for as long as this process runs the device runtime.
/// A second process on the same database cannot get it and stays a read-only standby. When the connection breaks the lock is
/// gone server-side; <see cref="CheckAsync"/> reconnects and re-acquires it (or reports it lost to another process).
/// </summary>
public sealed partial class RuntimeLease(IDatabaseConnectionProvider connections, ILogger<RuntimeLease> logger) : IAsyncDisposable
{
    public const string Resource = "ControlHome.Runtime";

    private const string AcquireSql = """
        DECLARE @result int;
        EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0;
        SELECT @result;
        """;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqlConnection? _connection;

    /// <summary>Whether this process holds the lease (as far as it knows).</summary>
    public bool IsHeld => _connection is not null;

    /// <summary>
    /// Tries to take the lease without waiting: true when this process holds it, false when another one does.
    /// </summary>
    /// <exception cref="SqlException">The database cannot be reached.</exception>
    public async Task<bool> TryAcquireAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await AcquireLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Verifies the lease is still held, reconnecting and re-acquiring it when the connection broke.</summary>
    public async Task<LeaseState> CheckAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is { State: ConnectionState.Open } connection)
            {
                try
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT APPLOCK_MODE('public', @resource, 'Session');";
                    command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = Resource });
                    var mode = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
                    if (string.Equals(mode, "Exclusive", StringComparison.OrdinalIgnoreCase))
                    {
                        return LeaseState.Held;
                    }
                }
                catch (Exception ex) when (ex is SqlException or InvalidOperationException)
                {
                    LogConnectionLost(ex.GetType().Name);
                }
            }

            try
            {
                return await AcquireLockedAsync(cancellationToken).ConfigureAwait(false) ? LeaseState.Held : LeaseState.Lost;
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException)
            {
                LogReconnectFailed(ex.GetType().Name);
                return LeaseState.Unreachable;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Releases the lease (closing the connection releases it too).</summary>
    public async Task ReleaseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync() => await ReleaseAsync().ConfigureAwait(false);

    private async Task<bool> AcquireLockedAsync(CancellationToken cancellationToken)
    {
        await CloseLockedAsync().ConfigureAwait(false);

        var connectionString = connections.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection is configured.");
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            // A session of its own: a pooled connection could be reset (releasing the lock) or shared.
            Pooling = false,
            ApplicationName = "ControlHome runtime lease",
        };
        var connection = new SqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = AcquireSql;
            command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = Resource });
            var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);

            // 0: granted; 1: granted after waiting; -1: timeout (held elsewhere); other negatives: errors.
            if (result >= 0)
            {
                _connection = connection;
                return true;
            }
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        await connection.DisposeAsync().ConfigureAwait(false);
        return false;
    }

    private async Task CloseLockedAsync()
    {
        if (_connection is not { } connection)
        {
            return;
        }

        _connection = null;
        try
        {
            if (connection.State == ConnectionState.Open)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';";
                command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = Resource });
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            // Closing the session releases the lock anyway.
            LogReleaseFailed(ex.GetType().Name);
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The runtime lease connection was lost ({ExceptionType}); re-acquiring")]
    private partial void LogConnectionLost(string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Re-acquiring the runtime lease failed ({ExceptionType}); the runtime keeps running")]
    private partial void LogReconnectFailed(string exceptionType);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Releasing the runtime lease failed ({ExceptionType})")]
    private partial void LogReleaseFailed(string exceptionType);
}

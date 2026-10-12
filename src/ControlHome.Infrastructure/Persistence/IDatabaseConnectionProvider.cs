using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace ControlHome.Infrastructure.Persistence;

/// <summary>Where the database connection string comes from.</summary>
public enum DatabaseConnectionSource
{
    /// <summary>Nothing configured: pre-database setup mode.</summary>
    None,

    /// <summary>Configuration: env <c>ConnectionStrings__ControlHome</c> (user-secrets in Development).</summary>
    Environment,

    /// <summary><c>{DataPath}/config/database.json</c>, saved by the setup wizard.</summary>
    File,
}

/// <summary>Where the database connection string comes from. Never log the returned value.</summary>
public interface IDatabaseConnectionProvider
{
    /// <summary>Which source <see cref="GetConnectionString"/> currently answers from.</summary>
    DatabaseConnectionSource Source { get; }

    /// <summary>The connection string, or <c>null</c> when none is configured (pre-database setup mode).</summary>
    string? GetConnectionString();

    /// <summary>Fires when the configured connection changes (a connection saved by <see cref="TrySave"/>).</summary>
    IChangeToken GetChangeToken();

    /// <summary>
    /// Saves <paramref name="connectionString"/> to <c>{DataPath}/config/database.json</c> and fires the change token, but
    /// only while no connection is configured; returns false (and changes nothing) otherwise.
    /// </summary>
    bool TrySave(string connectionString);
}

/// <summary>
/// Resolution order: configuration <c>ConnectionStrings:ControlHome</c> (env <c>ConnectionStrings__ControlHome</c>,
/// user-secrets in Development), then <c>{DataPath}/config/database.json</c> (<see cref="DatabaseConfigFile"/>). The file is
/// read once and cached; nothing is read until first asked.
/// </summary>
public sealed class DatabaseConnectionProvider(IConfiguration configuration, DatabaseConfigFile file) : IDatabaseConnectionProvider
{
    public const string ConnectionStringName = "ControlHome";

    private readonly Lock _lock = new();
    private CancellationTokenSource _changed = new();
    private bool _fileRead;
    private string? _fromFile;

    public DatabaseConnectionSource Source =>
        FromConfiguration() is not null ? DatabaseConnectionSource.Environment
        : FromFile() is not null ? DatabaseConnectionSource.File
        : DatabaseConnectionSource.None;

    public string? GetConnectionString() => FromConfiguration() ?? FromFile();

    public IChangeToken GetChangeToken()
    {
        lock (_lock)
        {
            return new CancellationChangeToken(_changed.Token);
        }
    }

    public bool TrySave(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        CancellationTokenSource changed;
        lock (_lock)
        {
            if (FromConfiguration() is not null || FromFileLocked() is not null)
            {
                return false;
            }

            file.Write(connectionString);
            _fromFile = connectionString;
            changed = _changed;
            _changed = new CancellationTokenSource();
        }

        // Outside the lock: the callbacks (the initializer) may read the connection string right away.
        changed.Cancel();
        return true;
    }

    private string? FromConfiguration()
    {
        var value = configuration.GetConnectionString(ConnectionStringName);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private string? FromFile()
    {
        lock (_lock)
        {
            return FromFileLocked();
        }
    }

    private string? FromFileLocked()
    {
        if (!_fileRead)
        {
            _fromFile = file.Read();
            _fileRead = true;
        }

        return _fromFile;
    }
}

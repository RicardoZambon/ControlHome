using ControlHome.Application.Abstractions;

namespace ControlHome.Infrastructure.Persistence;

/// <summary>The database's startup state machine.</summary>
public enum DatabaseState
{
    /// <summary>No connection string configured (waiting for the setup wizard to save one).</summary>
    NotConfigured,

    /// <summary>Waiting for the server (retrying with back-off).</summary>
    Connecting,

    /// <summary>Creating the database, applying migrations, seeding.</summary>
    Migrating,

    /// <summary>Usable.</summary>
    Ready,

    /// <summary>
    /// The last attempt failed; <see cref="DatabaseReadiness.LastError"/> has the error code. Retried every
    /// <see cref="DatabaseOptions.RetryInterval"/>.
    /// </summary>
    Failed,
}

/// <summary>Singleton holding the current <see cref="DatabaseState"/>; set by <see cref="DatabaseInitializer"/>.</summary>
public sealed class DatabaseReadiness
{
    private readonly Lock _lock = new();
    private TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DatabaseState _state = DatabaseState.NotConfigured;
    private string? _lastError;

    public DatabaseState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    /// <summary>The error code (from <see cref="Application.Common.ErrorCodes"/>) of the last failure, if any.</summary>
    public string? LastError
    {
        get
        {
            lock (_lock)
            {
                return _lastError;
            }
        }
    }

    public bool IsReady => State == DatabaseState.Ready;

    public void SetState(DatabaseState state, string? error = null)
    {
        TaskCompletionSource? toComplete = null;
        lock (_lock)
        {
            if (state != DatabaseState.Ready && _state == DatabaseState.Ready)
            {
                _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _state = state;
            _lastError = error;
            if (state == DatabaseState.Ready)
            {
                toComplete = _ready;
            }
        }

        toComplete?.TrySetResult();
    }

    /// <summary>Completes when the state becomes <see cref="DatabaseState.Ready"/>.</summary>
    public Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return _ready.Task.WaitAsync(cancellationToken);
        }
    }
}

/// <summary><see cref="IDatabaseStatus"/> for the Application layer.</summary>
public sealed class DatabaseStatus(DatabaseReadiness readiness, IDatabaseConnectionProvider connections) : IDatabaseStatus
{
    public bool IsConfigured => connections.GetConnectionString() is not null;

    public string Source => connections.Source switch
    {
        DatabaseConnectionSource.Environment => "environment",
        DatabaseConnectionSource.File => "file",
        _ => "none",
    };

    public bool IsReady => readiness.IsReady;

    public string State => readiness.State.ToString();

    public string? LastError => readiness.LastError;
}

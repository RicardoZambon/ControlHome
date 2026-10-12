namespace ControlHome.Infrastructure.Persistence;

/// <summary>
/// Thrown when something needs the database before it is <see cref="DatabaseState.Ready"/> (e.g. the Data Protection key
/// ring during startup). Expected while the database starts, so it is not logged as an error.
/// </summary>
public sealed class DatabaseNotReadyException : InvalidOperationException
{
    public DatabaseNotReadyException()
        : this(DatabaseState.NotConfigured)
    {
    }

    public DatabaseNotReadyException(DatabaseState state)
        : base($"The database is not ready yet (state {state}).") => State = state;

    public DatabaseNotReadyException(string message)
        : base(message)
    {
    }

    public DatabaseNotReadyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public DatabaseState State { get; }
}

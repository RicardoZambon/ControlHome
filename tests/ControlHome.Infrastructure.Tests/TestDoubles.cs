using System.Threading.Channels;
using ControlHome.Infrastructure.Persistence;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Time.Testing;

namespace ControlHome.Infrastructure.Tests;

/// <summary>An in-memory <see cref="IDatabaseConnectionProvider"/> whose connection string the test sets (and signals).</summary>
internal sealed class FakeConnectionProvider(string? connectionString = null) : IDatabaseConnectionProvider
{
    private readonly Lock _lock = new();
    private CancellationTokenSource _changed = new();
    private string? _connectionString = connectionString;

    public DatabaseConnectionSource Source => GetConnectionString() is null ? DatabaseConnectionSource.None : DatabaseConnectionSource.File;

    public string? GetConnectionString()
    {
        lock (_lock)
        {
            return _connectionString;
        }
    }

    public IChangeToken GetChangeToken()
    {
        lock (_lock)
        {
            return new CancellationChangeToken(_changed.Token);
        }
    }

    public bool TrySave(string connectionString)
    {
        lock (_lock)
        {
            if (_connectionString is not null)
            {
                return false;
            }
        }

        Set(connectionString);
        return true;
    }

    /// <summary>Replaces the connection string and fires the change token.</summary>
    public void Set(string? connectionString)
    {
        CancellationTokenSource previous;
        lock (_lock)
        {
            _connectionString = connectionString;
            previous = _changed;
            _changed = new CancellationTokenSource();
        }

        previous.Cancel();
    }
}

/// <summary>A real probe that can be switched "down" (it then fails like an unreachable server).</summary>
internal sealed class SwitchableProbe(ISqlServerProbe inner) : ISqlServerProbe
{
    private volatile bool _down;

    public bool Down
    {
        get => _down;
        set => _down = value;
    }

    public Task<SqlServerFacts> ProbeAsync(string connectionString, string databaseName, CancellationToken cancellationToken) =>
        _down
            ? Task.FromException<SqlServerFacts>(new InvalidOperationException("The test switched the server off."))
            : inner.ProbeAsync(connectionString, databaseName, cancellationToken);
}

/// <summary>A <see cref="FakeTimeProvider"/> that reports every timer created on it (e.g. by <c>Task.Delay</c>).</summary>
internal sealed class RecordingTimeProvider : FakeTimeProvider
{
    private readonly Channel<TimeSpan> _timers = Channel.CreateUnbounded<TimeSpan>();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        _timers.Writer.TryWrite(dueTime);
        return timer;
    }

    /// <summary>The due time of the next timer created (waits at most 30 s for it).</summary>
    public async Task<TimeSpan> NextTimerAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await _timers.Reader.ReadAsync(timeout.Token);
    }
}

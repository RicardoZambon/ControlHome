using ControlHome.Application.Realtime;
using ControlHome.Application.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlHome.Infrastructure.Devices;

/// <summary>The runtime's mode and health (<see cref="IRuntimeStatus"/>); every change is pushed as hub <c>SystemStatusChanged</c>.</summary>
public sealed partial class DeviceRuntimeState(
    IRealtimePublisher publisher,
    IOptions<DeviceRuntimeOptions> options,
    ILogger<DeviceRuntimeState> logger) : IRuntimeStatus
{
    private readonly Lock _lock = new();
    private RuntimeMode _mode = RuntimeMode.Starting;
    private bool _historyPaused;
    private Task _publishing = Task.CompletedTask;

    /// <inheritdoc />
    public bool Enabled => options.Value.Enabled;

    public RuntimeMode Mode
    {
        get
        {
            lock (_lock)
            {
                return _mode;
            }
        }

        set
        {
            lock (_lock)
            {
                if (_mode == value)
                {
                    return;
                }

                _mode = value;
            }

            LogModeChanged(value);
            Publish();
        }
    }

    public bool HistoryPaused
    {
        get
        {
            lock (_lock)
            {
                return _historyPaused;
            }
        }

        set
        {
            lock (_lock)
            {
                if (_historyPaused == value)
                {
                    return;
                }

                _historyPaused = value;
            }

            if (value)
            {
                LogHistoryPaused();
            }
            else
            {
                LogHistoryResumed();
            }

            Publish();
        }
    }

    /// <summary>Pushes the status as it is when the push runs; pushes run one after the other, so the last one is current.</summary>
    private void Publish()
    {
        lock (_lock)
        {
            _publishing = _publishing.ContinueWith(
                    async _ =>
                    {
                        try
                        {
                            await publisher.SystemStatusChangedAsync(this.ToDto(), CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            LogPublishFailed(ex.GetType().Name);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Device runtime mode: {Mode}")]
    private partial void LogModeChanged(RuntimeMode mode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "State history is paused: the database is not keeping up, the oldest history rows were dropped")]
    private partial void LogHistoryPaused();

    [LoggerMessage(Level = LogLevel.Information, Message = "State history resumed")]
    private partial void LogHistoryResumed();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not publish the system status ({ExceptionType})")]
    private partial void LogPublishFailed(string exceptionType);
}

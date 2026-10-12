using ControlHome.Application.Realtime;
using ControlHome.Domain.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlHome.Infrastructure.Devices;

/// <summary>
/// Batches the pipeline's pushes to clients: state deltas (the latest per attribute), availability changes (the latest per
/// device) and <c>DevicesChanged</c> are sent together at most <see cref="DeviceRuntimeOptions.PublishWindow"/> after the
/// first of them, so a burst of reports becomes a few hub messages instead of thousands.
/// </summary>
public sealed partial class RealtimeBatcher : IDisposable
{
    private readonly IRealtimePublisher _publisher;
    private readonly ILogger<RealtimeBatcher> _logger;
    private readonly TimeSpan _window;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly ITimer _timer;
    private Dictionary<int, StateDelta> _deltas = [];
    private Dictionary<Guid, Availability> _availability = [];
    private bool _devicesChanged;
    private bool _scheduled;

    public RealtimeBatcher(
        IRealtimePublisher publisher,
        TimeProvider timeProvider,
        IOptions<DeviceRuntimeOptions> options,
        ILogger<RealtimeBatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        _publisher = publisher;
        _logger = logger;
        _window = options.Value.PublishWindow;
        _timer = BackgroundWork.WithoutFlow(() => timeProvider.CreateTimer(
            static s => _ = ((RealtimeBatcher)s!).FlushAsync(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan));
    }

    public void StateChanged(StateDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        lock (_lock)
        {
            _deltas[delta.AttributeId] = delta;
            ScheduleLocked();
        }
    }

    public void AvailabilityChanged(Guid deviceId, Availability availability)
    {
        lock (_lock)
        {
            _availability[deviceId] = availability;
            ScheduleLocked();
        }
    }

    public void DevicesChanged()
    {
        lock (_lock)
        {
            _devicesChanged = true;
            ScheduleLocked();
        }
    }

    /// <summary>Sends everything waiting now (also used when stopping and by tests).</summary>
    public async Task FlushAsync()
    {
        await _sending.WaitAsync().ConfigureAwait(false);
        try
        {
            Dictionary<int, StateDelta> deltas;
            Dictionary<Guid, Availability> availability;
            bool devicesChanged;
            lock (_lock)
            {
                deltas = _deltas;
                availability = _availability;
                devicesChanged = _devicesChanged;
                _deltas = [];
                _availability = [];
                _devicesChanged = false;
                _scheduled = false;
            }

            try
            {
                if (devicesChanged)
                {
                    await _publisher.DevicesChangedAsync(CancellationToken.None).ConfigureAwait(false);
                }

                foreach (var (deviceId, value) in availability)
                {
                    await _publisher.AvailabilityChangedAsync(new AvailabilityChangedDto(deviceId, value.ToString()), CancellationToken.None)
                        .ConfigureAwait(false);
                }

                if (deltas.Count > 0)
                {
                    await _publisher.StateChangedAsync([.. deltas.Values.OrderBy(d => d.AttributeId)], CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                LogPublishFailed(deltas.Count, ex.GetType().Name);
            }
        }
        finally
        {
            _sending.Release();
        }
    }

    /// <summary>Drops everything waiting (test reset).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _deltas.Clear();
            _availability.Clear();
            _devicesChanged = false;
        }
    }

    public void Dispose() => _timer.Dispose();

    private void ScheduleLocked()
    {
        if (_scheduled)
        {
            return;
        }

        _scheduled = true;
        _timer.Change(_window, Timeout.InfiniteTimeSpan);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publishing {Deltas} state deltas to clients failed ({ExceptionType})")]
    private partial void LogPublishFailed(int deltas, string exceptionType);
}

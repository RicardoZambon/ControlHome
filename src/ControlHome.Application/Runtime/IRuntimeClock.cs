namespace ControlHome.Application.Runtime;

/// <summary>
/// The clock of the device runtime: state timestamps, history and rollups use it. It is the app's
/// <see cref="TimeProvider"/>, except in the <c>Testing</c> environment, where tests move it forward
/// (<c>POST /api/_test/clock</c>) to produce hours of history in seconds.
/// </summary>
public interface IRuntimeClock
{
    TimeProvider Time { get; }
}

/// <summary>The runtime clock of hosts without a device runtime: the app's <see cref="TimeProvider"/>.</summary>
public sealed class AppRuntimeClock(TimeProvider time) : IRuntimeClock
{
    public TimeProvider Time { get; } = time;
}

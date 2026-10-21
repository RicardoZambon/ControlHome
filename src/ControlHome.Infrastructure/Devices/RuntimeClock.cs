using ControlHome.Application.Runtime;

namespace ControlHome.Infrastructure.Devices;

/// <summary>
/// The clock of the device runtime: connector contexts (<c>IConnectorContext.Time</c>: the Simulated home's ticks and command
/// latency), the timestamps of state reports without one, and the history rollups and retention. It is the app's
/// <see cref="TimeProvider"/>, except in the <c>Testing</c> environment, where it is an <see cref="AdjustableTimeProvider"/>
/// that <c>POST /api/_test/clock</c> moves forward. Kept apart from the app clock so jumping it never touches sign-in
/// lockouts, cookies or keys.
/// </summary>
public sealed class RuntimeClock(TimeProvider time) : IRuntimeClock
{
    public TimeProvider Time { get; } = time;
}

namespace ControlHome.Application.Auth;

/// <summary>
/// Escalating sign-in lockout: the n-th lockout in a row (no successful sign-in in between) lasts
/// <c>base × 2^n</c> (n starting at 0), capped at <see cref="MaxDuration"/>. It is never permanent and resets on success.
/// </summary>
public static class LockoutPolicy
{
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(1);

    /// <param name="baseDuration">The first lockout's length (Identity's <c>DefaultLockoutTimeSpan</c>, 5 minutes).</param>
    /// <param name="consecutiveLockouts">Lockouts in a row before this one.</param>
    public static TimeSpan Duration(TimeSpan baseDuration, int consecutiveLockouts)
    {
        if (baseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(baseDuration), baseDuration, "The base lockout must be positive.");
        }

        var duration = baseDuration;
        for (var i = 0; i < consecutiveLockouts && duration < MaxDuration; i++)
        {
            duration += duration;
        }

        return duration < MaxDuration ? duration : MaxDuration;
    }
}

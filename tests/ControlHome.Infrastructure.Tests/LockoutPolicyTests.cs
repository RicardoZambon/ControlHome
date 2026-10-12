using ControlHome.Application.Auth;

namespace ControlHome.Infrastructure.Tests;

/// <summary>Unit tests of the Application-layer lockout escalation (no database needed).</summary>
public sealed class LockoutPolicyTests
{
    private static readonly TimeSpan _base = TimeSpan.FromMinutes(5);

    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    [InlineData(4, 60)]
    [InlineData(5, 60)]
    [InlineData(int.MaxValue, 60)]
    public void Lockout_is_five_minutes_doubled_per_consecutive_lockout_capped_at_one_hour(int consecutiveLockouts, int expectedMinutes)
    {
        LockoutPolicy.Duration(_base, consecutiveLockouts).ShouldBe(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Fact]
    public void Negative_counts_are_treated_as_zero()
    {
        LockoutPolicy.Duration(_base, -3).ShouldBe(_base);
    }

    [Fact]
    public void Base_duration_must_be_positive()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => LockoutPolicy.Duration(TimeSpan.Zero, 1));
    }
}

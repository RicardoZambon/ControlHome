using ControlHome.Application.Security;
using Microsoft.Extensions.Time.Testing;

namespace ControlHome.Infrastructure.Tests.Safety;

public sealed class SafetyGrantStoreTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero));
    private readonly Guid _user = Guid.CreateVersion7();

    [Fact]
    public void A_grant_allows_exactly_one_command()
    {
        var store = new SafetyGrantStore(_time);
        store.Grant(_user);

        store.TryConsume(_user).ShouldBeTrue();
        store.TryConsume(_user).ShouldBeFalse();
    }

    [Fact]
    public void A_grant_expires_after_sixty_seconds()
    {
        var store = new SafetyGrantStore(_time);
        store.Grant(_user);

        _time.Advance(SafetyGrantStore.GrantLifetime);

        store.TryConsume(_user).ShouldBeFalse();
    }

    [Fact]
    public void Grants_are_per_user()
    {
        var store = new SafetyGrantStore(_time);
        store.Grant(_user);

        store.TryConsume(Guid.CreateVersion7()).ShouldBeFalse();
        store.TryConsume(_user).ShouldBeTrue();
    }

    [Fact]
    public void Five_failures_within_fifteen_minutes_lock_for_fifteen_minutes()
    {
        var store = new SafetyGrantStore(_time);
        for (var i = 0; i < SafetyGrantStore.MaxFailures - 1; i++)
        {
            store.RecordFailure(_user).ShouldBeNull();
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        store.RecordFailure(_user).ShouldBe(SafetyGrantStore.LockDuration);
        store.LockedFor(_user).ShouldBe(SafetyGrantStore.LockDuration);

        _time.Advance(SafetyGrantStore.LockDuration);
        store.LockedFor(_user).ShouldBeNull();
        store.RecordFailure(_user).ShouldBeNull();
    }

    [Fact]
    public void Failures_older_than_the_window_do_not_count()
    {
        var store = new SafetyGrantStore(_time);
        for (var i = 0; i < SafetyGrantStore.MaxFailures - 1; i++)
        {
            store.RecordFailure(_user);
        }

        _time.Advance(SafetyGrantStore.FailureWindow);

        store.RecordFailure(_user).ShouldBeNull();
    }

    [Fact]
    public void A_successful_step_up_forgets_earlier_failures_and_a_lockout_drops_the_grant()
    {
        var store = new SafetyGrantStore(_time);
        for (var i = 0; i < SafetyGrantStore.MaxFailures - 1; i++)
        {
            store.RecordFailure(_user);
        }

        store.Grant(_user);
        store.RecordFailure(_user).ShouldBeNull();

        for (var i = 0; i < SafetyGrantStore.MaxFailures - 1; i++)
        {
            store.RecordFailure(_user);
        }

        store.LockedFor(_user).ShouldNotBeNull();
        store.TryConsume(_user).ShouldBeFalse();
    }
}

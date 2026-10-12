using ControlHome.Infrastructure.Devices;

namespace ControlHome.Infrastructure.Tests.Devices.Runtime;

public sealed class AdjustableTimeProviderTests
{
    [Fact]
    public void It_runs_in_real_time_plus_the_offset()
    {
        var clock = new AdjustableTimeProvider();
        var before = DateTimeOffset.UtcNow;

        clock.Advance(TimeSpan.FromHours(2));

        var now = clock.GetUtcNow();
        now.ShouldBeGreaterThanOrEqualTo(before.AddHours(2));
        now.ShouldBeLessThan(DateTimeOffset.UtcNow.AddHours(2).AddSeconds(5));
        clock.Offset.ShouldBe(TimeSpan.FromHours(2));
    }

    [Fact]
    public async Task Advancing_fires_timers_that_became_due()
    {
        var clock = new AdjustableTimeProvider();
        var delay = Task.Delay(TimeSpan.FromHours(1), clock, TestContext.Current.CancellationToken);
        await Task.Delay(20, TestContext.Current.CancellationToken);
        delay.IsCompleted.ShouldBeFalse();

        clock.Advance(TimeSpan.FromHours(1));

        await delay.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Timers_still_fire_in_real_time()
    {
        var clock = new AdjustableTimeProvider();

        await Task.Delay(TimeSpan.FromMilliseconds(50), clock, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Periodic_timers_fire_once_per_period_that_passed()
    {
        var clock = new AdjustableTimeProvider();
        var ticks = 0;
        using var timer = clock.CreateTimer(_ => Interlocked.Increment(ref ticks), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        clock.Advance(TimeSpan.FromMinutes(1));
        await WaitUntilAsync(() => Volatile.Read(ref ticks) >= 1);
        clock.Advance(TimeSpan.FromMinutes(1));
        await WaitUntilAsync(() => Volatile.Read(ref ticks) >= 2);

        Volatile.Read(ref ticks).ShouldBe(2);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline);
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }
}

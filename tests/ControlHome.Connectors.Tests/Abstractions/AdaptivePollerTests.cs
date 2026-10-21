using ControlHome.Connectors.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ControlHome.Connectors.Tests.Abstractions;

public sealed class AdaptivePollerTests
{
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan _hot = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _window = TimeSpan.FromSeconds(30);

    [Fact]
    public void Polls_at_the_normal_interval_until_a_command_then_hot_for_the_window()
    {
        var time = new FakeTimeProvider();
        var poller = new AdaptivePoller(time, _interval, _hot, _window, jitter: 0);

        poller.NextDelay().ShouldBe(_interval);
        poller.IsHot.ShouldBeFalse();

        poller.NotifyCommand();
        poller.IsHot.ShouldBeTrue();
        poller.NextDelay().ShouldBe(_hot);

        time.Advance(_window - TimeSpan.FromSeconds(1));
        poller.NextDelay().ShouldBe(_hot);
        time.Advance(TimeSpan.FromSeconds(1));
        poller.IsHot.ShouldBeFalse();
        poller.NextDelay().ShouldBe(_interval);
    }

    [Fact]
    public void Jitter_stays_within_bounds_and_varies()
    {
        var poller = new AdaptivePoller(new FakeTimeProvider(), _interval, _hot, _window, jitter: 0.2, new Random(1));

        var delays = Enumerable.Range(0, 200).Select(_ => poller.NextDelay()).ToList();

        delays.ShouldAllBe(d => d >= _interval * 0.8 && d <= _interval * 1.2);
        delays.Distinct().Count().ShouldBeGreaterThan(100);
    }

    [Fact]
    public async Task Wait_completes_after_the_delay()
    {
        var time = new FakeTimeProvider();
        var poller = new AdaptivePoller(time, _interval, _hot, _window, jitter: 0);

        var waiting = poller.WaitAsync(TestContext.Current.CancellationToken);
        time.Advance(_interval - TimeSpan.FromSeconds(1));
        await Task.Delay(20, TestContext.Current.CancellationToken);
        waiting.IsCompleted.ShouldBeFalse();

        time.Advance(TimeSpan.FromSeconds(1));
        await waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_command_wakes_a_pending_wait_immediately()
    {
        var time = new FakeTimeProvider();
        var poller = new AdaptivePoller(time, _interval, _hot, _window, jitter: 0);

        var waiting = poller.WaitAsync(TestContext.Current.CancellationToken);
        poller.NotifyCommand();

        await waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        poller.NextDelay().ShouldBe(_hot);
    }

    [Fact]
    public async Task Wait_honours_cancellation()
    {
        var poller = new AdaptivePoller(new FakeTimeProvider(), _interval, _hot, _window, jitter: 0);
        using var cancellation = new CancellationTokenSource();

        var waiting = poller.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Rejects_inconsistent_settings()
    {
        var time = new FakeTimeProvider();

        Should.Throw<ArgumentOutOfRangeException>(() => new AdaptivePoller(time, _hot, _interval, _window));
        Should.Throw<ArgumentOutOfRangeException>(() => new AdaptivePoller(time, _interval, _hot, _window, jitter: 0.9));
        Should.Throw<ArgumentOutOfRangeException>(() => new AdaptivePoller(time, TimeSpan.Zero, _hot, _window));
    }
}

using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using ControlHome.Infrastructure.Devices;
using static ControlHome.Infrastructure.Tests.Devices.Runtime.PipelineHarness;

namespace ControlHome.Infrastructure.Tests.Devices.Runtime;

public sealed class StateWriteBehindTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Flush_writes_the_latest_value_per_attribute_history_in_order_and_merged_device_updates()
    {
        await using var pipeline = new PipelineHarness();
        var writeBehind = pipeline.WriteBehind;
        var deviceId = Guid.CreateVersion7();
        writeBehind.EnqueueState(new PendingAttributeState(1, StateValue.Number(1), T0, T0));
        writeBehind.EnqueueState(new PendingAttributeState(1, StateValue.Number(2), T0.AddSeconds(1), T0.AddSeconds(1)));
        writeBehind.EnqueueHistory(new PendingHistoryRow(1, T0, StateValue.Number(1)));
        writeBehind.EnqueueHistory(new PendingHistoryRow(1, T0.AddSeconds(1), StateValue.Number(2)));
        writeBehind.EnqueueDevice(new PendingDeviceUpdate(deviceId, Availability.Online, null));
        writeBehind.EnqueueDevice(new PendingDeviceUpdate(deviceId, null, T0.AddSeconds(2)));

        (await writeBehind.FlushAsync(Ct)).ShouldBeTrue();

        var batch = pipeline.Store.Batches.ShouldHaveSingleItem();
        batch.States.ShouldHaveSingleItem().Value.ShouldBe(StateValue.Number(2));
        batch.History.Select(h => h.Value.N).ShouldBe([1d, 2d]);
        batch.Devices.ShouldBe([new PendingDeviceUpdate(deviceId, Availability.Online, T0.AddSeconds(2))]);
        (writeBehind.PendingStates, writeBehind.PendingHistory, writeBehind.PendingDevices).ShouldBe((0, 0, 0));
    }

    [Fact]
    public async Task Availability_left_over_from_a_failed_flush_is_dropped_before_a_restart_but_last_seen_is_kept()
    {
        await using var pipeline = new PipelineHarness();
        var writeBehind = pipeline.WriteBehind;
        var online = Guid.CreateVersion7();
        var seen = Guid.CreateVersion7();
        pipeline.Store.Fail = true;
        writeBehind.EnqueueDevice(new PendingDeviceUpdate(online, Availability.Online, null));
        writeBehind.EnqueueDevice(new PendingDeviceUpdate(seen, Availability.Offline, T0));
        (await writeBehind.FlushAsync(Ct)).ShouldBeFalse();

        writeBehind.DropPendingAvailability();

        pipeline.Store.Fail = false;
        (await writeBehind.FlushAsync(Ct)).ShouldBeTrue();
        pipeline.Store.Batches.ShouldHaveSingleItem().Devices.ShouldBe([new PendingDeviceUpdate(seen, null, T0)]);
    }

    [Fact]
    public async Task While_the_database_is_down_everything_stays_buffered_and_is_written_once_it_is_back()
    {
        await using var pipeline = new PipelineHarness();
        var writeBehind = pipeline.WriteBehind;
        pipeline.Store.Fail = true;
        writeBehind.EnqueueState(new PendingAttributeState(1, StateValue.True, T0, T0));
        writeBehind.EnqueueHistory(new PendingHistoryRow(1, T0, StateValue.True));

        (await writeBehind.FlushAsync(Ct)).ShouldBeFalse();
        (await writeBehind.FlushAsync(Ct)).ShouldBeFalse();

        (writeBehind.PendingStates, writeBehind.PendingHistory).ShouldBe((1, 1));
        pipeline.Store.Fail = false;
        (await writeBehind.FlushAsync(Ct)).ShouldBeTrue();
        var batch = pipeline.Store.Batches.ShouldHaveSingleItem();
        (batch.States.Count, batch.History.Count).ShouldBe((1, 1));
    }

    [Fact]
    public async Task A_failed_flush_never_overwrites_a_value_that_arrived_meanwhile()
    {
        await using var pipeline = new PipelineHarness();
        var writeBehind = pipeline.WriteBehind;
        writeBehind.EnqueueState(new PendingAttributeState(1, StateValue.Number(1), T0, T0));
        pipeline.Store.Fail = true;
        pipeline.Store.DuringWrite = () =>
            writeBehind.EnqueueState(new PendingAttributeState(1, StateValue.Number(2), T0.AddSeconds(1), T0.AddSeconds(1)));

        (await writeBehind.FlushAsync(Ct)).ShouldBeFalse();
        pipeline.Store.Fail = false;
        pipeline.Store.DuringWrite = null;
        (await writeBehind.FlushAsync(Ct)).ShouldBeTrue();

        pipeline.Store.Batches.ShouldHaveSingleItem().States.ShouldHaveSingleItem().Value.ShouldBe(StateValue.Number(2));
    }

    [Fact]
    public async Task Beyond_the_limit_the_oldest_history_is_dropped_and_history_is_paused_until_a_flush_succeeds()
    {
        await using var pipeline = new PipelineHarness(o => o.MaxPendingHistoryRows = 5);
        var writeBehind = pipeline.WriteBehind;
        pipeline.Store.Fail = true;
        for (var i = 0; i < 8; i++)
        {
            writeBehind.EnqueueState(new PendingAttributeState(i, StateValue.Number(i), T0.AddSeconds(i), T0.AddSeconds(i)));
            writeBehind.EnqueueHistory(new PendingHistoryRow(i, T0.AddSeconds(i), StateValue.Number(i)));
        }

        (await writeBehind.FlushAsync(Ct)).ShouldBeFalse();

        writeBehind.PendingHistory.ShouldBe(5);
        writeBehind.DroppedHistory.ShouldBe(3);
        writeBehind.PendingStates.ShouldBe(8); // current state is never dropped
        pipeline.RuntimeState.HistoryPaused.ShouldBeTrue();
        await RecordingPublisher.WaitUntilAsync(
            () => pipeline.Publisher.SystemStatuses.Any(s => s.HistoryPaused),
            "SystemStatusChanged(historyPaused) is published");

        pipeline.Store.Fail = false;
        (await writeBehind.FlushAsync(Ct)).ShouldBeTrue();

        pipeline.Store.Batches.ShouldHaveSingleItem().History.Select(h => h.AttributeId).ShouldBe([3, 4, 5, 6, 7]);
        pipeline.RuntimeState.HistoryPaused.ShouldBeFalse();
    }

    [Fact]
    public async Task The_default_limit_is_fifty_thousand_rows()
    {
        await using var pipeline = new PipelineHarness();
        var writeBehind = pipeline.WriteBehind;
        pipeline.Store.Fail = true;
        for (var i = 0; i < 50_010; i++)
        {
            writeBehind.EnqueueHistory(new PendingHistoryRow(1, T0.AddMilliseconds(i), StateValue.Number(i)));
        }

        (await writeBehind.FlushAsync(Ct)).ShouldBeFalse();

        writeBehind.PendingHistory.ShouldBe(50_000);
        writeBehind.DroppedHistory.ShouldBe(10);
        pipeline.RuntimeState.HistoryPaused.ShouldBeTrue();
    }

    [Fact]
    public async Task The_loop_flushes_every_interval()
    {
        await using var pipeline = new PipelineHarness(o => o.FlushInterval = TimeSpan.FromSeconds(5));
        var writeBehind = pipeline.WriteBehind;
        writeBehind.Start();
        writeBehind.EnqueueState(new PendingAttributeState(1, StateValue.True, T0, T0));

        // The loop's timer is created on a background task: keep moving the clock until it has ticked.
        await RecordingPublisher.WaitUntilAsync(
            () =>
            {
                pipeline.Time.Advance(TimeSpan.FromSeconds(5));
                return pipeline.Store.Batches.Count == 1;
            },
            "the periodic flush ran");

        await writeBehind.StopAsync(Ct);
        writeBehind.PendingStates.ShouldBe(0);
    }
}

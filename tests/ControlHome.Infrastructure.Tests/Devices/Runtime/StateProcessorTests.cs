using ControlHome.Application.Realtime;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using ControlHome.Infrastructure.Devices;
using static ControlHome.Infrastructure.Tests.Devices.Runtime.PipelineHarness;

namespace ControlHome.Infrastructure.Tests.Devices.Runtime;

public sealed class StateProcessorTests
{
    [Fact]
    public async Task A_report_updates_the_cache_publishes_a_delta_queues_the_write_and_notifies_subscribers()
    {
        await using var pipeline = new PipelineHarness();

        pipeline.Processor.ReportState(pipeline.IntegrationId, pipeline.Report(CapabilityIds.Switch, "on", StateValue.True));
        await pipeline.SettleAsync();

        pipeline.Cache.TryGet(OnAttribute, out var cached).ShouldBeTrue();
        cached.Value.ShouldBe(StateValue.True);
        pipeline.Publisher.Deltas.ShouldBe([new StateDelta(OnAttribute, StateValue.True, T0)]);
        pipeline.WriteBehind.PendingStates.ShouldBe(1);
        var change = pipeline.Subscriber.Changes.ShouldHaveSingleItem();
        (change.AttributeId, change.Value, change.Changed, change.IsEvent).ShouldBe((OnAttribute, StateValue.True, true, false));
        change.PreviousValue.ShouldBeNull();
    }

    [Fact]
    public async Task A_duplicate_report_is_deduplicated()
    {
        await using var pipeline = new PipelineHarness();
        pipeline.Processor.ReportState(pipeline.IntegrationId, pipeline.Report(CapabilityIds.Level, "level", StateValue.Number(40)));
        await pipeline.SettleAsync();

        pipeline.Processor.ReportState(pipeline.IntegrationId, pipeline.Report(CapabilityIds.Level, "level", StateValue.Number(40)));
        await pipeline.SettleAsync();

        pipeline.Publisher.Deltas.Count.ShouldBe(1);
        pipeline.Subscriber.Changes.Select(c => c.Changed).ShouldBe([true, false]);
    }

    [Fact]
    public async Task An_out_of_order_report_is_dropped()
    {
        await using var pipeline = new PipelineHarness();
        pipeline.Processor.ReportState(pipeline.IntegrationId, pipeline.Report(CapabilityIds.Level, "level", StateValue.Number(80), T0.AddSeconds(10)));
        await pipeline.SettleAsync();

        pipeline.Processor.ReportState(pipeline.IntegrationId, pipeline.Report(CapabilityIds.Level, "level", StateValue.Number(20), T0));
        await pipeline.SettleAsync();

        pipeline.Processor.OutOfOrder.ShouldBe(1);
        pipeline.Cache.TryGet(LevelAttribute, out var cached).ShouldBeTrue();
        cached.Value.ShouldBe(StateValue.Number(80));
        pipeline.Publisher.Deltas.ShouldHaveSingleItem().Value.ShouldBe(StateValue.Number(80));
    }

    [Fact]
    public async Task Reports_waiting_for_the_same_attribute_are_coalesced_latest_wins()
    {
        await using var pipeline = new PipelineHarness();
        for (var i = 1; i <= 100; i++)
        {
            pipeline.Processor.ReportState(pipeline.IntegrationId, pipeline.Report(CapabilityIds.Level, "level", StateValue.Number(i), T0.AddMilliseconds(i)));
        }

        await pipeline.SettleAsync();

        pipeline.Processor.Received.ShouldBe(100);
        pipeline.Processor.Coalesced.ShouldBe(99);
        pipeline.Publisher.Deltas.ShouldHaveSingleItem().Value.ShouldBe(StateValue.Number(100));
    }

    [Fact]
    public async Task A_full_queue_drops_the_oldest_waiting_attribute()
    {
        await using var pipeline = new PipelineHarness(o => o.QueueCapacity = 10);
        for (var i = 0; i < 15; i++)
        {
            pipeline.Processor.ReportState(
                pipeline.IntegrationId,
                new StateReport(DeviceExternalId, Endpoint.MainKey, CapabilityIds.Generic, $"a{i}", StateValue.Number(i), T0));
        }

        await pipeline.SettleAsync();

        // The first five waiting attributes were dropped; only the last ten were looked up (and are unknown).
        pipeline.Inventory.Lookups.Select(a => a.Attribute).ShouldBe(Enumerable.Range(5, 10).Select(i => $"a{i}"));
        pipeline.Processor.Dropped.ShouldBe(15);
    }

    [Fact]
    public async Task Momentary_events_are_never_coalesced()
    {
        await using var pipeline = new PipelineHarness();
        foreach (var name in new[] { "single", "double", "single" })
        {
            pipeline.Processor.ReportEvent(
                pipeline.IntegrationId,
                new EventReport(DeviceExternalId, Endpoint.MainKey, CapabilityIds.Switch, "on", name, null, T0));
        }

        await pipeline.SettleAsync();

        pipeline.Subscriber.Changes.Where(c => c.IsEvent).Select(c => c.Value.S).ShouldBe(["single", "double", "single"]);
    }

    [Fact]
    public async Task An_unknown_attribute_is_looked_up_once_and_dropped()
    {
        await using var pipeline = new PipelineHarness();
        var report = new StateReport(DeviceExternalId, Endpoint.MainKey, CapabilityIds.Switch, "nope", StateValue.True, T0);

        pipeline.Processor.ReportState(pipeline.IntegrationId, report);
        await pipeline.SettleAsync();
        pipeline.Processor.ReportState(pipeline.IntegrationId, report with { ObservedAtUtc = T0.AddSeconds(1) });
        await pipeline.SettleAsync();

        pipeline.Inventory.Lookups.ShouldHaveSingleItem().Attribute.ShouldBe("nope");
        pipeline.Processor.Dropped.ShouldBe(2);
        pipeline.Publisher.Deltas.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_new_attribute_of_a_known_capability_is_created_through_the_inventory()
    {
        await using var pipeline = new PipelineHarness();
        var address = new AttributeAddress(pipeline.IntegrationId, DeviceExternalId, Endpoint.MainKey, CapabilityIds.Level, "extra");
        pipeline.Inventory.Resolve = a => a == address
            ? new AttributeInfo(99, a, pipeline.DeviceId, Guid.CreateVersion7(), 11, null)
            : null;

        pipeline.Processor.ReportState(pipeline.IntegrationId, new StateReport(DeviceExternalId, Endpoint.MainKey, CapabilityIds.Level, "extra", StateValue.Number(1), T0));
        await pipeline.SettleAsync();

        pipeline.Publisher.Deltas.ShouldHaveSingleItem().AttributeId.ShouldBe(99);
    }

    [Fact]
    public async Task A_value_the_schema_rejects_is_dropped()
    {
        await using var pipeline = new PipelineHarness();

        pipeline.Processor.ReportState(pipeline.IntegrationId, pipeline.Report(CapabilityIds.Switch, "on", StateValue.String("yes")));
        pipeline.Processor.ReportState(pipeline.IntegrationId, pipeline.Report(CapabilityIds.Level, "level", StateValue.Number(250)));
        await pipeline.SettleAsync();

        pipeline.Publisher.Deltas.ShouldBeEmpty();
        pipeline.Processor.Dropped.ShouldBe(2);
    }

    [Fact]
    public async Task Changes_of_recorded_attributes_also_queue_a_history_row()
    {
        await using var pipeline = new PipelineHarness();

        pipeline.Processor.ReportState(pipeline.IntegrationId, pipeline.Report(CapabilityIds.Power, "power", StateValue.Number(60)));
        pipeline.Processor.ReportState(pipeline.IntegrationId, pipeline.Report(CapabilityIds.Switch, "on", StateValue.True));
        await pipeline.SettleAsync();
        pipeline.Processor.ReportState(pipeline.IntegrationId, pipeline.Report(CapabilityIds.Switch, "on", StateValue.True, T0.AddSeconds(5)));
        await pipeline.SettleAsync();

        pipeline.WriteBehind.PendingStates.ShouldBe(2);
        pipeline.WriteBehind.PendingHistory.ShouldBe(2); // an unchanged value adds no history
    }

    [Fact]
    public async Task Availability_changes_are_published_and_persisted_once()
    {
        await using var pipeline = new PipelineHarness();

        pipeline.Processor.ReportAvailability(pipeline.IntegrationId, DeviceExternalId, Availability.Online);
        await pipeline.SettleAsync();
        pipeline.Processor.ReportAvailability(pipeline.IntegrationId, DeviceExternalId, Availability.Online);
        await pipeline.SettleAsync();

        pipeline.Publisher.Availability.ShouldBe([new AvailabilityChangedDto(pipeline.DeviceId, "Online")]);
        pipeline.WriteBehind.PendingDevices.ShouldBe(1);
        pipeline.Registry.FindDevice(pipeline.DeviceId)!.Availability.ShouldBe(Availability.Online);
    }

    [Fact]
    public async Task Deltas_reported_together_are_published_in_one_batch()
    {
        await using var pipeline = new PipelineHarness();

        pipeline.Processor.ReportState(pipeline.IntegrationId, pipeline.Report(CapabilityIds.Switch, "on", StateValue.True));
        pipeline.Processor.ReportState(pipeline.IntegrationId, pipeline.Report(CapabilityIds.Level, "level", StateValue.Number(55)));
        pipeline.Processor.Start();
        await pipeline.Processor.WhenIdleAsync(TimeSpan.FromSeconds(10));
        pipeline.Publisher.Deltas.ShouldBeEmpty(); // still inside the publish window

        pipeline.Time.Advance(pipeline.Options.Value.PublishWindow);
        await RecordingPublisher.WaitUntilAsync(() => pipeline.Publisher.DeltaBatches.Count > 0, "the batch is published");

        pipeline.Publisher.DeltaBatches.ShouldHaveSingleItem().Select(d => d.AttributeId).ShouldBe([OnAttribute, LevelAttribute]);
    }
}

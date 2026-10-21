using System.Text.Json;
using ControlHome.Application.Realtime;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using ControlHome.Infrastructure.Devices;
using Microsoft.Extensions.Logging.Abstractions;
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
    public async Task An_older_report_never_replaces_a_newer_one_waiting_for_the_same_attribute()
    {
        await using var pipeline = new PipelineHarness();

        // The loop is not running yet (SettleAsync starts it): both wait together.
        pipeline.Processor.ReportState(pipeline.IntegrationId, pipeline.Report(CapabilityIds.Level, "level", StateValue.Number(80), T0.AddSeconds(10)));
        pipeline.Processor.ReportState(pipeline.IntegrationId, pipeline.Report(CapabilityIds.Level, "level", StateValue.Number(20), T0));
        await pipeline.SettleAsync();

        pipeline.Cache.TryGet(LevelAttribute, out var cached).ShouldBeTrue();
        cached.Value.ShouldBe(StateValue.Number(80));
        pipeline.Publisher.Deltas.ShouldHaveSingleItem().Value.ShouldBe(StateValue.Number(80));
        pipeline.Processor.OutOfOrder.ShouldBe(1);
        pipeline.Processor.Coalesced.ShouldBe(0);
    }

    [Fact]
    public async Task A_failing_lookup_does_not_stall_other_reports_and_is_retried_only_after_a_back_off()
    {
        await using var pipeline = new PipelineHarness();
        pipeline.Inventory.ResolveAsync = (_, _) => Task.FromException<AttributeInfo?>(new TimeoutException("The database is down."));
        var unknown = new StateReport(DeviceExternalId, Endpoint.MainKey, CapabilityIds.Level, "vendorKey", StateValue.Number(1), T0);

        pipeline.Processor.ReportState(pipeline.IntegrationId, unknown);
        pipeline.Processor.ReportState(pipeline.IntegrationId, pipeline.Report(CapabilityIds.Switch, "on", StateValue.True));
        await pipeline.SettleAsync();

        pipeline.Subscriber.Changes.ShouldHaveSingleItem().AttributeId.ShouldBe(OnAttribute);
        pipeline.Inventory.Lookups.Count.ShouldBe(1);

        // Within the back-off: dropped without asking again.
        pipeline.Time.Advance(StateProcessor.ResolveFailureBackoff - TimeSpan.FromSeconds(1));
        pipeline.Processor.ReportState(pipeline.IntegrationId, unknown with { ObservedAtUtc = T0.AddSeconds(1) });
        await pipeline.SettleAsync();
        pipeline.Inventory.Lookups.Count.ShouldBe(1);

        // After it: asked again; a second failure doubles the back-off.
        pipeline.Time.Advance(TimeSpan.FromSeconds(1));
        pipeline.Processor.ReportState(pipeline.IntegrationId, unknown with { ObservedAtUtc = T0.AddSeconds(2) });
        await pipeline.SettleAsync();
        pipeline.Inventory.Lookups.Count.ShouldBe(2);

        pipeline.Time.Advance(StateProcessor.ResolveFailureBackoff);
        pipeline.Processor.ReportState(pipeline.IntegrationId, unknown with { ObservedAtUtc = T0.AddSeconds(3) });
        await pipeline.SettleAsync();
        pipeline.Inventory.Lookups.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_hanging_lookup_is_abandoned_after_the_resolve_timeout()
    {
        await using var pipeline = new PipelineHarness();
        pipeline.Inventory.ResolveAsync = async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        };

        pipeline.Processor.ReportState(
            pipeline.IntegrationId,
            new StateReport(DeviceExternalId, Endpoint.MainKey, CapabilityIds.Level, "vendorKey", StateValue.Number(1), T0));
        pipeline.Processor.ReportState(pipeline.IntegrationId, pipeline.Report(CapabilityIds.Switch, "on", StateValue.True));
        pipeline.Processor.Start();
        await RecordingPublisher.WaitUntilAsync(() => pipeline.Inventory.Lookups.Count == 1, "the lookup started");
        pipeline.Subscriber.Changes.ShouldBeEmpty();

        pipeline.Time.Advance(StateProcessor.ResolveTimeout);
        await pipeline.SettleAsync();

        pipeline.Subscriber.Changes.ShouldHaveSingleItem().AttributeId.ShouldBe(OnAttribute);
    }

    [Fact]
    public async Task Clearing_while_reports_arrive_never_strands_an_attribute()
    {
        await using var pipeline = new PipelineHarness();
        pipeline.Processor.Start();
        using var stop = new CancellationTokenSource();
        var producer = Task.Run(
            () =>
            {
                for (var i = 0; !stop.IsCancellationRequested; i++)
                {
                    pipeline.Processor.ReportState(pipeline.IntegrationId, pipeline.Report(CapabilityIds.Level, "level", StateValue.Number(i % 100), T0.AddMilliseconds(i)));
                }
            },
            TestContext.Current.CancellationToken);
        for (var i = 0; i < 200; i++)
        {
            pipeline.Processor.Clear();
        }

        await stop.CancelAsync();
        await producer;

        // Whatever the interleaving, the attribute is still read: a later report reaches the cache.
        pipeline.Processor.ReportState(pipeline.IntegrationId, pipeline.Report(CapabilityIds.Level, "level", StateValue.Number(42), T0.AddDays(1)));
        await pipeline.SettleAsync();
        pipeline.Cache.TryGet(LevelAttribute, out var cached).ShouldBeTrue();
        cached.Value.ShouldBe(StateValue.Number(42));
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
    public async Task Momentary_events_queue_event_rows_with_their_payload()
    {
        await using var pipeline = new PipelineHarness();
        using var data = JsonDocument.Parse("""{"count":2}""");
        pipeline.Processor.ReportEvent(
            pipeline.IntegrationId,
            new EventReport(DeviceExternalId, Endpoint.MainKey, CapabilityIds.Switch, "on", "double", data.RootElement, T0));
        pipeline.Processor.ReportEvent(
            pipeline.IntegrationId,
            new EventReport(DeviceExternalId, Endpoint.MainKey, CapabilityIds.Switch, "on", "single", null, T0.AddSeconds(1)));
        pipeline.Processor.ReportState(pipeline.IntegrationId, pipeline.Report(CapabilityIds.Power, "power", StateValue.Number(60)));
        await pipeline.SettleAsync();

        (await pipeline.WriteBehind.FlushAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        var history = pipeline.Store.Batches.ShouldHaveSingleItem().History;
        var events = history.Where(h => h.IsEvent).ToList();
        events.Select(e => (e.AttributeId, e.Value.S, e.EventPayload)).ShouldBe(
            [(OnAttribute, "double", """{"count":2}"""), (OnAttribute, "single", null)]);
        history.Where(h => !h.IsEvent).ShouldHaveSingleItem().AttributeId.ShouldBe(PowerAttribute);
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
    public async Task Removing_a_device_makes_it_unknown_after_any_availability_reported_before()
    {
        await using var pipeline = new PipelineHarness();
        var context = new ConnectorContext(
            pipeline.IntegrationId,
            pipeline.Time,
            NullLogger.Instance,
            pipeline.Processor,
            pipeline.Inventory,
            (_, _) => { });
        context.ReportAvailability(DeviceExternalId, Availability.Online, null);
        await pipeline.SettleAsync();

        // Offline still waiting in the queue, then the connector drops the device.
        context.ReportAvailability(DeviceExternalId, Availability.Offline, null);
        await context.RemoveDeviceAsync(DeviceExternalId, TestContext.Current.CancellationToken);
        await pipeline.SettleAsync();

        pipeline.Inventory.Removed.ShouldBe([DeviceExternalId]);
        pipeline.Registry.FindDevice(pipeline.DeviceId)!.Availability.ShouldBe(Availability.Unknown);
        pipeline.Publisher.Availability[^1].Availability.ShouldBe(nameof(Availability.Unknown));
        (await pipeline.WriteBehind.FlushAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        pipeline.Store.Batches[^1].Devices.Single(d => d.DeviceId == pipeline.DeviceId).Availability.ShouldBe(Availability.Unknown);
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

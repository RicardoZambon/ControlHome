using System.Text.Json;
using ControlHome.Application.Integrations;
using ControlHome.Connectors.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ControlHome.Infrastructure.Tests.Devices;

/// <summary>Flows in progress live in memory for 15 minutes after their last step, a few per admin, and are disposed when dropped.</summary>
public sealed class IntegrationFlowStoreTests
{
    private static readonly Guid _owner = Guid.NewGuid();

    [Fact]
    public async Task A_flow_expires_fifteen_minutes_after_its_last_step_and_is_disposed()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
        var store = new IntegrationFlowStore(time);
        var flow = new CountingFlow();
        var session = new FlowSession(Guid.NewGuid(), "simulated", null, _owner, flow);
        await store.AddAsync(session);

        time.Advance(TimeSpan.FromMinutes(14));
        store.Find(session.Id, _owner).ShouldBeSameAs(session);
        store.Touch(session);
        time.Advance(TimeSpan.FromMinutes(14));
        store.Find(session.Id, _owner).ShouldBeSameAs(session, "a step extends the lifetime");
        store.Find(session.Id, Guid.NewGuid()).ShouldBeNull("another admin's flow is not found");

        time.Advance(TimeSpan.FromMinutes(1));
        store.Find(session.Id, _owner).ShouldBeNull();
        flow.Disposed.ShouldBe(0, "disposed on the next sweep");
        await store.AddAsync(new FlowSession(Guid.NewGuid(), "simulated", null, _owner, new CountingFlow()));
        flow.Disposed.ShouldBe(1);
        store.Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_admin_keeps_at_most_ten_flows_the_oldest_are_dropped()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
        var store = new IntegrationFlowStore(time);
        var flows = new List<(FlowSession Session, CountingFlow Flow)>();
        for (var i = 0; i < IntegrationFlowStore.MaxFlowsPerUser + 2; i++)
        {
            var flow = new CountingFlow();
            var session = new FlowSession(Guid.NewGuid(), "simulated", null, _owner, flow);
            await store.AddAsync(session);
            flows.Add((session, flow));
            time.Advance(TimeSpan.FromSeconds(1));
        }

        await store.AddAsync(new FlowSession(Guid.NewGuid(), "simulated", null, Guid.NewGuid(), new CountingFlow()));

        store.Count.ShouldBe(IntegrationFlowStore.MaxFlowsPerUser + 1);
        flows.Take(2).ShouldAllBe(f => f.Flow.Disposed == 1 && store.Find(f.Session.Id, _owner) == null);
        flows.Skip(2).ShouldAllBe(f => f.Flow.Disposed == 0 && store.Find(f.Session.Id, _owner) != null);

        await store.RemoveAsync(flows[5].Session.Id);
        flows[5].Flow.Disposed.ShouldBe(1);
        await store.ClearAsync();
        store.Count.ShouldBe(0);
    }

    private sealed class CountingFlow : IConnectorFlow
    {
        public int Disposed { get; private set; }

        public Task<FlowStep> StartAsync(FlowContext context, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FlowStep> ContinueAsync(string stepId, JsonElement input, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            Disposed++;
            return ValueTask.CompletedTask;
        }
    }
}

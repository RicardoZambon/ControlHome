using System.Text.Json;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Auditing;
using ControlHome.Application.Devices;
using ControlHome.Application.Integrations;
using ControlHome.Application.Runtime;
using ControlHome.Application.Security;
using ControlHome.Connectors.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ControlHome.Infrastructure.Tests.Devices;

/// <summary>Flows answer one step at a time; cancelling waits for the step in progress.</summary>
public sealed class IntegrationFlowServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Cancelling_waits_for_the_step_in_progress_so_the_flow_is_never_disposed_under_it()
    {
        var flow = new GatedFlow();
        var (service, store) = Service(flow);
        var flowId = (await service.StartAsync(new StartFlowRequest(GatedFlow.Key), Ct)).Value.ShouldNotBeNull().FlowId;

        var answering = service.ContinueAsync(flowId, new ContinueFlowRequest(null), Ct);
        await flow.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var cancelling = service.CancelAsync(flowId, Ct);

        cancelling.IsCompleted.ShouldBeFalse("a cancel waits for the answer being processed");
        flow.Disposed.ShouldBeFalse();

        flow.Release.SetResult();
        (await answering).IsSuccess.ShouldBeTrue();
        (await cancelling).IsSuccess.ShouldBeTrue();
        flow.DisposedWhileRunning.ShouldBeFalse();
        flow.Disposed.ShouldBeTrue();
        store.Count.ShouldBe(0);
        (await service.ContinueAsync(flowId, new ContinueFlowRequest(null), Ct)).Error.ShouldNotBeNull().Code.ShouldBe("flows.not_found");
    }

    private static (IntegrationFlowService Service, IntegrationFlowStore Store) Service(IConnectorFlow flow)
    {
        var descriptor = Substitute.For<IConnectorDescriptor>();
        descriptor.Key.Returns(GatedFlow.Key);
        descriptor.DisplayName.Returns(new LocalizedText("Gated", "Com portão"));
        descriptor.ConfigFields.Returns([]);
        var factory = Substitute.For<IConnectorFactory>();
        factory.Descriptor.Returns(descriptor);
        factory.CreateSetupFlow().Returns(flow);
        var status = Substitute.For<IRuntimeStatus>();
        status.Mode.Returns(RuntimeMode.Active);
        status.Enabled.Returns(true);
        var actor = Substitute.For<ICurrentActor>();
        actor.UserId.Returns(Guid.NewGuid());
        var store = new IntegrationFlowStore(TimeProvider.System);
        var service = new IntegrationFlowService(
            Substitute.For<IControlHomeDb>(),
            new ConnectorCatalog([factory]),
            store,
            Substitute.For<IIntegrationSecretProtector>(),
            NoIntegrationSecretStores.Instance,
            Substitute.For<IIntegrationRuntimeControl>(),
            status,
            new AppRuntimeClock(TimeProvider.System),
            actor,
            Substitute.For<IAuditLog>(),
            new KnownSecrets(),
            TimeProvider.System,
            NullLogger<IntegrationFlowService>.Instance);
        return (service, store);
    }

    /// <summary>A flow whose answers wait until <see cref="Release"/> is set.</summary>
    private sealed class GatedFlow : IConnectorFlow
    {
        public const string Key = "gated";

        private volatile bool _running;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed { get; private set; }

        public bool DisposedWhileRunning { get; private set; }

        public Task<FlowStep> StartAsync(FlowContext context, CancellationToken cancellationToken) => Task.FromResult(Form());

        public async Task<FlowStep> ContinueAsync(string stepId, JsonElement input, CancellationToken cancellationToken)
        {
            _running = true;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            _running = false;
            return Form();
        }

        public ValueTask DisposeAsync()
        {
            DisposedWhileRunning |= _running;
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        private static FlowStep Form() => new FormStep("step", new LocalizedText("Step", "Etapa"), null, []);
    }
}

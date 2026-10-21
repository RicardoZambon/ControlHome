using ControlHome.Application.Abstractions;
using ControlHome.Application.Auditing;
using ControlHome.Application.Common;
using ControlHome.Application.Devices;
using ControlHome.Application.Integrations;
using ControlHome.Application.Realtime;
using ControlHome.Application.Runtime;
using ControlHome.Application.Security;
using NSubstitute;

namespace ControlHome.Infrastructure.Tests.Devices;

/// <summary>
/// Integration changes are made only where they will be applied: a standby (409 <c>runtime.standby</c>) and a process with a
/// device runtime that is still deciding whether it is active (503 <c>runtime.starting</c>) refuse them; a process without a
/// device runtime does not.
/// </summary>
public sealed class IntegrationServiceRuntimeModeTests
{
    [Theory]
    [InlineData(RuntimeMode.Standby, true, ErrorCodes.RuntimeStandby, AppErrorKind.Conflict)]
    [InlineData(RuntimeMode.Starting, true, ErrorCodes.RuntimeStarting, AppErrorKind.Unavailable)]
    public async Task Changes_are_refused_unless_this_process_runs_the_devices(RuntimeMode mode, bool enabled, string code, AppErrorKind kind)
    {
        var (service, runtime) = Service(mode, enabled);

        var result = await service.CreateAsync(new CreateIntegrationRequest("simulated", "Simulated", null), TestContext.Current.CancellationToken);

        result.Error.ShouldNotBeNull();
        result.Error.Code.ShouldBe(code);
        result.Error.Kind.ShouldBe(kind);
        await runtime.DidNotReceive().StartAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(RuntimeMode.Standby, ErrorCodes.RuntimeStandby)]
    [InlineData(RuntimeMode.Starting, ErrorCodes.RuntimeStarting)]
    public async Task Flows_are_refused_unless_this_process_runs_the_devices(RuntimeMode mode, string code)
    {
        var status = Substitute.For<IRuntimeStatus>();
        status.Mode.Returns(mode);
        status.Enabled.Returns(true);
        var runtime = Substitute.For<IIntegrationRuntimeControl>();
        var store = new IntegrationFlowStore(TimeProvider.System);
        var flows = new IntegrationFlowService(
            Substitute.For<IControlHomeDb>(),
            new ConnectorCatalog([]),
            store,
            Substitute.For<IIntegrationSecretProtector>(),
            NoIntegrationSecretStores.Instance,
            runtime,
            status,
            new AppRuntimeClock(TimeProvider.System),
            Substitute.For<ICurrentActor>(),
            Substitute.For<IAuditLog>(),
            new KnownSecrets(),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IntegrationFlowService>.Instance);

        var started = await flows.StartAsync(new StartFlowRequest("simulated"), TestContext.Current.CancellationToken);
        var continued = await flows.ContinueAsync(Guid.NewGuid(), new ContinueFlowRequest(null), TestContext.Current.CancellationToken);

        started.Error.ShouldNotBeNull().Code.ShouldBe(code);
        continued.Error.ShouldNotBeNull().Code.ShouldBe(ErrorCodes.FlowsNotFound, "an unknown flow is unknown whatever the mode");
        store.Count.ShouldBe(0);
        await runtime.DidNotReceive().StartAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(RuntimeMode.Standby, ErrorCodes.RuntimeStandby)]
    [InlineData(RuntimeMode.Starting, ErrorCodes.RuntimeStarting)]
    public async Task Device_replacements_are_refused_unless_this_process_runs_the_devices(RuntimeMode mode, string code)
    {
        var status = Substitute.For<IRuntimeStatus>();
        status.Mode.Returns(mode);
        status.Enabled.Returns(true);
        var inventory = Substitute.For<IDeviceInventoryAdmin>();
        var db = Substitute.For<IControlHomeDb>();
        var devices = new DeviceAdminService(
            db,
            new DeviceQueryService(db, Substitute.For<IAttributeStateReader>(), NoLiveDeviceStatus.Instance),
            inventory,
            status,
            Substitute.For<IRealtimePublisher>(),
            Substitute.For<IAuditLog>());

        // The active process keeps the devices in memory: a standby deleting one and moving its attributes would leave it
        // writing to attributes that no longer exist.
        var result = await devices.ReplaceAsync(Guid.NewGuid(), new ReplaceDeviceRequest(Guid.NewGuid()), TestContext.Current.CancellationToken);

        result.Error.ShouldNotBeNull().Code.ShouldBe(code);
        await inventory.DidNotReceive().ReplaceAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_a_device_runtime_nothing_is_refused_for_the_mode()
    {
        var (service, _) = Service(RuntimeMode.Starting, enabled: false);

        var result = await service.CreateAsync(new CreateIntegrationRequest("simulated", "Simulated", null), TestContext.Current.CancellationToken);

        // Reaches the connector lookup (the catalogue here is empty).
        result.Error.ShouldNotBeNull();
        result.Error.Kind.ShouldBe(AppErrorKind.Validation);
    }

    private static (IntegrationService Service, IIntegrationRuntimeControl Runtime) Service(RuntimeMode mode, bool enabled)
    {
        var status = Substitute.For<IRuntimeStatus>();
        status.Mode.Returns(mode);
        status.Enabled.Returns(enabled);
        var runtime = Substitute.For<IIntegrationRuntimeControl>();
        var db = Substitute.For<IControlHomeDb>();
        var service = new IntegrationService(
            db,
            new ConnectorCatalog([]),
            Substitute.For<IIntegrationSecretProtector>(),
            runtime,
            status,
            new DeviceQueryService(db, Substitute.For<IAttributeStateReader>(), NoLiveDeviceStatus.Instance),
            Substitute.For<IRealtimePublisher>(),
            Substitute.For<IAuditLog>(),
            TimeProvider.System);
        return (service, runtime);
    }
}

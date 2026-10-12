using System.Collections.Concurrent;
using System.Text.Json;
using ControlHome.Application.Auditing;
using ControlHome.Application.Common;
using ControlHome.Application.Devices;
using ControlHome.Application.Realtime;
using ControlHome.Application.Security;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Auditing;
using ControlHome.Domain.Capabilities;
using ControlHome.Infrastructure.Tests.Devices.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ControlHome.Infrastructure.Tests.Commands;

public sealed class CommandTrackerTests : IDisposable
{
    private static readonly DateTimeOffset _t0 = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly AuditActorSnapshot _actor = new(AuditActorType.User, Guid.CreateVersion7(), "maria", "192.168.1.20");

    private readonly FakeTimeProvider _time = new(_t0);
    private readonly FakeDispatcher _dispatcher = new();
    private readonly RecordingPublisher _publisher = new();
    private readonly RecordingAuditLog _audit = new();
    private readonly ServiceProvider _services;
    private readonly CommandTracker _tracker;

    public CommandTrackerTests()
    {
        _services = new ServiceCollection().AddSingleton<IAuditLog>(_audit).BuildServiceProvider();
        _tracker = new CommandTracker(
            _dispatcher,
            _publisher,
            _services.GetRequiredService<IServiceScopeFactory>(),
            new KnownSecrets(),
            _time,
            NullLogger<CommandTracker>.Instance);
    }

    public void Dispose()
    {
        _tracker.Dispose();
        _services.Dispose();
    }

    [Fact]
    public async Task A_completed_command_is_pending_then_confirmed_and_audited()
    {
        var command = Command(CapabilityIds.Switch, "turnOn");

        _tracker.Submit(command);

        await WaitForStatusAsync(command.CommandId, CommandStatuses.Confirmed);
        StatusesOf(command.CommandId).ShouldBe([CommandStatuses.Pending, CommandStatuses.Confirmed]);
        _dispatcher.Batches.ShouldHaveSingleItem().ShouldBe(command.Batch);
        await RecordingPublisher.WaitUntilAsync(() => _audit.Events.Count == 1, "the command is audited");
        var audit = _audit.Events.ShouldHaveSingleItem();
        audit.Action.ShouldBe(AuditActions.CommandExecuted);
        audit.TargetId.ShouldBe(command.EndpointId.ToString());
        audit.Actor.ShouldBe(_actor);
        JsonSerializer.SerializeToElement(audit.Details, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .GetProperty("outcome").GetString().ShouldBe(CommandStatuses.Confirmed);
    }

    [Theory]
    [InlineData(CommandRejectionCodes.Duplicate, ErrorCodes.CommandsDuplicate)]
    [InlineData(CommandRejectionCodes.Refused, ErrorCodes.CommandsRefused)]
    [InlineData(CommandRejectionCodes.Unavailable, ErrorCodes.CommandsUnavailable)]
    [InlineData(CommandRejectionCodes.UnknownDevice, ErrorCodes.CommandsUnsupported)]
    [InlineData(CommandRejectionCodes.IdempotencyRequired, ErrorCodes.CommandsFailed)]
    [InlineData("vendor_specific", ErrorCodes.CommandsFailed)]
    public async Task A_rejected_command_fails_with_a_commands_code(string rejection, string code)
    {
        _dispatcher.Outcome = _ => CommandOutcome.Reject(rejection, "no");
        var command = Command(CapabilityIds.Switch, "turnOn");

        _tracker.Submit(command);

        var status = await WaitForStatusAsync(command.CommandId, CommandStatuses.Failed);
        status.Code.ShouldBe(code);
    }

    [Fact]
    public async Task An_accepted_await_state_command_is_confirmed_by_a_report_of_its_capability()
    {
        _dispatcher.Outcome = _ => CommandOutcome.AcceptedOutcome;
        var command = Command(CapabilityIds.Switch, "turnOn", attributeIds: [41]);

        _tracker.Submit(command);
        await RecordingPublisher.WaitUntilAsync(() => _dispatcher.Batches.Count == 1, "the command is dispatched");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        StatusesOf(command.CommandId).ShouldBe([CommandStatuses.Pending]);

        await _tracker.OnStateChangedAsync(
            [new AttributeStateChange(41, StateValue.False, StateValue.True, _t0, Changed: true, IsEvent: false)],
            TestContext.Current.CancellationToken);

        await WaitForStatusAsync(command.CommandId, CommandStatuses.Confirmed);
    }

    [Fact]
    public async Task An_accepted_command_without_a_report_times_out()
    {
        _dispatcher.Outcome = _ => CommandOutcome.AcceptedOutcome;
        var command = Command(CapabilityIds.Switch, "turnOn", attributeIds: [41]) with { Timeout = TimeSpan.FromSeconds(5) };

        _tracker.Submit(command);
        await RecordingPublisher.WaitUntilAsync(() => _dispatcher.Batches.Count == 1, "the command is dispatched");
        _time.Advance(TimeSpan.FromSeconds(5));

        var status = await WaitForStatusAsync(command.CommandId, CommandStatuses.TimedOut);
        status.Code.ShouldBe(ErrorCodes.CommandsTimedOut);
    }

    [Fact]
    public async Task An_accepted_ack_is_final_command_is_confirmed_at_once()
    {
        _dispatcher.Outcome = _ => CommandOutcome.AcceptedOutcome;
        var definition = CapabilityCatalog.DoorOpener.FindCommand("open")!;
        var command = Command(CapabilityIds.DoorOpener, "open") with { Definition = definition };

        _tracker.Submit(command);

        await WaitForStatusAsync(command.CommandId, CommandStatuses.Confirmed);
    }

    [Fact]
    public async Task Setters_for_one_control_are_throttled_and_the_last_waiting_one_wins()
    {
        var endpointId = Guid.CreateVersion7();
        var first = Setter(endpointId, 10);
        var second = Setter(endpointId, 20);
        var third = Setter(endpointId, 30);
        var fourth = Setter(endpointId, 40);

        _tracker.Submit(first);
        await RecordingPublisher.WaitUntilAsync(() => _dispatcher.Batches.Count == 1, "the first setter is sent at once");
        _tracker.Submit(second);
        _tracker.Submit(third);
        _tracker.Submit(fourth);

        await WaitForStatusAsync(second.CommandId, CommandStatuses.Superseded);
        await WaitForStatusAsync(third.CommandId, CommandStatuses.Superseded);
        _dispatcher.Batches.Count.ShouldBe(1);

        _time.Advance(CommandTracker.CoalesceWindow);

        await WaitForStatusAsync(fourth.CommandId, CommandStatuses.Confirmed);
        await WaitForStatusAsync(first.CommandId, CommandStatuses.Confirmed);
        _dispatcher.Batches.Select(b => b.Commands[0].Arguments.GetProperty("level").GetInt32()).ShouldBe([10, 40]);
    }

    [Fact]
    public async Task Setters_of_different_controls_are_not_coalesced()
    {
        _tracker.Submit(Setter(Guid.CreateVersion7(), 10));
        _tracker.Submit(Setter(Guid.CreateVersion7(), 20));

        await RecordingPublisher.WaitUntilAsync(() => _dispatcher.Batches.Count == 2, "both setters are sent at once");
    }

    [Fact]
    public void Only_non_safety_critical_setters_are_coalescable()
    {
        CommandTracker.IsCoalescable(CapabilityCatalog.Level.FindCommand("setLevel")!, safetyCritical: false).ShouldBeTrue();
        CommandTracker.IsCoalescable(CapabilityCatalog.Color.FindCommand("setColorTemperature")!, safetyCritical: false).ShouldBeTrue();
        CommandTracker.IsCoalescable(CapabilityCatalog.Cover.FindCommand("setPosition")!, safetyCritical: true).ShouldBeFalse();
        CommandTracker.IsCoalescable(CapabilityCatalog.Switch.FindCommand("turnOn")!, safetyCritical: false).ShouldBeFalse();
    }

    [Fact]
    public void Idempotency_keys_are_remembered_for_ten_minutes()
    {
        _tracker.IsKnownKey("k1").ShouldBeFalse();
        _tracker.TryReserveKey("k1").ShouldBeTrue();
        _tracker.TryReserveKey("k1").ShouldBeFalse();
        _tracker.IsKnownKey("k1").ShouldBeTrue();

        _time.Advance(CommandTracker.IdempotencyWindow);

        _tracker.IsKnownKey("k1").ShouldBeFalse();
        _tracker.TryReserveKey("k1").ShouldBeTrue();
    }

    private static TrackedCommand Command(string capabilityId, string name, IReadOnlyList<int>? attributeIds = null, JsonElement? arguments = null)
    {
        var definition = CapabilityCatalog.Find(capabilityId)!.FindCommand(name)!;
        return new TrackedCommand(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            capabilityId,
            definition,
            attributeIds ?? [1],
            new DeviceCommandBatch("dev-1", [new DeviceCommand("main", capabilityId, name, arguments ?? JsonDocument.Parse("{}").RootElement)], Guid.NewGuid().ToString("N")),
            TimeSpan.FromSeconds(5),
            Coalescable: false,
            _actor,
            AuditArguments: true);
    }

    private static TrackedCommand Setter(Guid endpointId, int level) =>
        Command(CapabilityIds.Level, "setLevel", arguments: JsonDocument.Parse($$"""{"level":{{level}}}""").RootElement) with
        {
            EndpointId = endpointId,
            Coalescable = true,
        };

    private List<string> StatusesOf(Guid commandId) =>
        _publisher.Statuses.Where(s => s.CommandId == commandId).Select(s => s.Status).ToList();

    private async Task<CommandStatusDto> WaitForStatusAsync(Guid commandId, string status)
    {
        await RecordingPublisher.WaitUntilAsync(
            () => _publisher.Statuses.Any(s => s.CommandId == commandId && s.Status == status),
            $"command {commandId} is {status}");
        return _publisher.Statuses.First(s => s.CommandId == commandId && s.Status == status);
    }

    /// <summary>Records batches and answers with <see cref="Outcome"/> (completed by default).</summary>
    private sealed class FakeDispatcher : ICommandDispatcher
    {
        private readonly ConcurrentQueue<DeviceCommandBatch> _batches = new();

        public Func<DeviceCommandBatch, CommandOutcome> Outcome { get; set; } = _ => CommandOutcome.CompletedOutcome;

        public IReadOnlyList<DeviceCommandBatch> Batches => [.. _batches];

        public bool IsRunning(Guid integrationId) => true;

        public Task<CommandOutcome> DispatchAsync(Guid integrationId, DeviceCommandBatch batch, CancellationToken cancellationToken)
        {
            _batches.Enqueue(batch);
            return Task.FromResult(Outcome(batch));
        }
    }
}

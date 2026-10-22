using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using ControlHome.Application.Auditing;
using ControlHome.Application.Common;
using ControlHome.Application.Realtime;
using ControlHome.Application.Security;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Capabilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ControlHome.Application.Devices;

/// <summary>An accepted command on its way to the connector.</summary>
/// <param name="Confirmation">Which state reports confirm (or fail) an awaitState command the connector only accepted.</param>
/// <param name="Timeout">How long to wait for the connector (and, for awaitState commands, the confirming state report).</param>
/// <param name="Coalescable">A setter that may be replaced by a later one for the same endpoint capability (see <see cref="CommandTracker"/>).</param>
/// <param name="AuditArguments">Whether the arguments go into the audit entry (not for safety-critical commands).</param>
public sealed record TrackedCommand(
    Guid CommandId,
    Guid IntegrationId,
    Guid EndpointId,
    string CapabilityId,
    CommandDefinition Definition,
    CommandConfirmation Confirmation,
    DeviceCommandBatch Batch,
    TimeSpan Timeout,
    bool Coalescable,
    AuditActorSnapshot Actor,
    bool AuditArguments);

/// <summary>
/// Runs accepted commands in the background and reports their progress (hub <c>CommandStatus</c>, audit
/// <c>commands.executed</c>):
/// <list type="bullet">
/// <item><b>At most once:</b> each batch is dispatched exactly once (never retried); idempotency keys are remembered for
/// <see cref="IdempotencyWindow"/>, so a repeated key is refused before it reaches the connector.</item>
/// <item><b>Coalescing:</b> setters (<c>setLevel</c>, <c>setVolume</c>, <c>setPosition</c>, <c>setTargetTemperature</c>,
/// <c>setColor*</c>) for the same endpoint capability are throttled to one dispatch per <see cref="CoalesceWindow"/>: the
/// first is sent at once, later ones within the window wait, and only the last waiting one is sent when the window ends
/// (the ones it replaced report <c>superseded</c>). Any other command for the same endpoint capability (e.g.
/// <c>cover.close</c> after <c>setPosition</c>) supersedes the setter still waiting, so the latest intent always wins.</item>
/// <item><b>Confirmation:</b> <c>Completed</c> confirms; <c>Accepted</c> confirms at once for <c>ackIsFinal</c> commands
/// and, for <c>awaitState</c> ones, when the device reports the target state (<see cref="CommandConfirmation"/>; a failing
/// state such as <c>jammed</c> → <c>failed</c> with <c>commands.not_confirmed</c>); no answer within the timeout →
/// <c>timed_out</c>; <c>Rejected</c> → <c>failed</c> with a <c>commands.*</c> code.</item>
/// </list>
/// </summary>
public sealed partial class CommandTracker : IStateChangeSubscriber, IDisposable
{
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan IdempotencyWindow = TimeSpan.FromMinutes(10);

    /// <summary>Confirmation timeout when the command defines none: local connectors.</summary>
    public static readonly TimeSpan DefaultLocalTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Confirmation timeout when the command defines none: cloud connectors.</summary>
    public static readonly TimeSpan DefaultCloudTimeout = TimeSpan.FromSeconds(30);

    public const string EndpointTarget = "endpoint";

    private static readonly string[] _coalescableCommands = ["setLevel", "setVolume", "setPosition", "setTargetTemperature"];

    private readonly ICommandDispatcher _dispatcher;
    private readonly IRealtimePublisher _publisher;
    private readonly IServiceScopeFactory _scopes;
    private readonly KnownSecrets _secrets;
    private readonly TimeProvider _time;
    private readonly ILogger<CommandTracker> _logger;
    private readonly CancellationTokenSource _stopping = new();

    private readonly Lock _keysLock = new();
    private readonly Dictionary<string, DateTimeOffset> _keys = new(StringComparer.Ordinal);
    private readonly Queue<(string Key, DateTimeOffset At)> _keyOrder = new();

    private readonly Lock _windowsLock = new();
    private readonly Dictionary<(Guid EndpointId, string CapabilityId), Window> _windows = [];

    private readonly Lock _waitersLock = new();
    private readonly Dictionary<int, List<Waiter>> _waiters = [];

    private readonly Channel<CommandStatusDto> _statuses = Channel.CreateUnbounded<CommandStatusDto>(new UnboundedChannelOptions { SingleReader = true });

    private int _running;

    public CommandTracker(
        ICommandDispatcher dispatcher,
        IRealtimePublisher publisher,
        IServiceScopeFactory scopes,
        KnownSecrets secrets,
        TimeProvider time,
        ILogger<CommandTracker> logger)
    {
        _dispatcher = dispatcher;
        _publisher = publisher;
        _scopes = scopes;
        _secrets = secrets;
        _time = time;
        _logger = logger;
        _ = WithoutFlow(() => Task.Run(PublishLoopAsync));
    }

    /// <summary>Commands dispatched and not finished yet.</summary>
    public int Running => Volatile.Read(ref _running);

    /// <summary>Whether a command may be coalesced (setters, never safety-critical ones).</summary>
    public static bool IsCoalescable(CommandDefinition command, bool safetyCritical)
    {
        ArgumentNullException.ThrowIfNull(command);
        return !safetyCritical
            && (_coalescableCommands.Contains(command.Name, StringComparer.Ordinal)
                || command.Name.StartsWith("setColor", StringComparison.Ordinal));
    }

    /// <summary>The <c>commands.*</c> error code for a connector's rejection code.</summary>
    public static string CodeFor(string rejectionCode) => rejectionCode switch
    {
        CommandRejectionCodes.Duplicate => ErrorCodes.CommandsDuplicate,
        CommandRejectionCodes.Unavailable => ErrorCodes.CommandsUnavailable,
        CommandRejectionCodes.Refused => ErrorCodes.CommandsRefused,
        CommandRejectionCodes.InvalidArguments => ErrorCodes.CommandsInvalidArguments,
        CommandRejectionCodes.UnsupportedCommand or CommandRejectionCodes.UnknownEndpoint or CommandRejectionCodes.UnknownDevice =>
            ErrorCodes.CommandsUnsupported,
        _ => ErrorCodes.CommandsFailed,
    };

    /// <summary>Whether <paramref name="key"/> was used within <see cref="IdempotencyWindow"/>.</summary>
    public bool IsKnownKey(string key)
    {
        lock (_keysLock)
        {
            PurgeKeys(_time.GetUtcNow());
            return _keys.ContainsKey(key);
        }
    }

    /// <summary>Marks <paramref name="key"/> used; false when it already was (a duplicate).</summary>
    public bool TryReserveKey(string key)
    {
        lock (_keysLock)
        {
            var now = _time.GetUtcNow();
            PurgeKeys(now);
            if (!_keys.TryAdd(key, now))
            {
                return false;
            }

            _keyOrder.Enqueue((key, now));
            return true;
        }
    }

    /// <summary>Reports <c>pending</c> and sends the command (now, or at the end of its coalescing window).</summary>
    public void Submit(TrackedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        Publish(new CommandStatusDto(command.CommandId, CommandStatuses.Pending, null));

        if (!command.Coalescable)
        {
            // A setter still waiting for this control would go out after this command and undo it (close, then reopen):
            // the latest intent wins. The window stays open, so a setter sent right after this one is still throttled.
            TrackedCommand? dropped = null;
            lock (_windowsLock)
            {
                if (_windows.TryGetValue((command.EndpointId, command.CapabilityId), out var open))
                {
                    dropped = open.Held;
                    open.Held = null;
                }
            }

            Start(command);
            if (dropped is not null)
            {
                Finish(dropped, CommandStatuses.Superseded, null, null);
            }

            return;
        }

        TrackedCommand? superseded = null;
        var startNow = false;
        var key = (command.EndpointId, command.CapabilityId);
        lock (_windowsLock)
        {
            if (_windows.TryGetValue(key, out var window))
            {
                superseded = window.Held;
                window.Held = command;
            }
            else
            {
                window = new Window();
                _windows[key] = window;
                window.Timer = WithoutFlow(() => _time.CreateTimer(OnWindowElapsed, key, CoalesceWindow, Timeout.InfiniteTimeSpan));
                startNow = true;
            }
        }

        if (startNow)
        {
            Start(command);
        }

        if (superseded is not null)
        {
            Finish(superseded, CommandStatuses.Superseded, null, null);
        }
    }

    public ValueTask OnStateChangedAsync(IReadOnlyList<AttributeStateChange> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        List<(Waiter Waiter, ConfirmationState State)>? done = null;
        lock (_waitersLock)
        {
            if (_waiters.Count == 0)
            {
                return ValueTask.CompletedTask;
            }

            foreach (var change in changes)
            {
                if (!_waiters.TryGetValue(change.AttributeId, out var waiters))
                {
                    continue;
                }

                foreach (var waiter in waiters.ToList())
                {
                    if (waiter.Finished)
                    {
                        continue;
                    }

                    var state = waiter.Progress.Apply(change);
                    if (state != ConfirmationState.Pending)
                    {
                        waiter.Finished = true;
                        (done ??= []).Add((waiter, state));
                    }
                }
            }
        }

        if (done is not null)
        {
            foreach (var (waiter, state) in done)
            {
                waiter.Done.TrySetResult(state);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Forgets keys, coalescing windows and waiters (test reset; running commands still finish).</summary>
    public void Reset()
    {
        lock (_keysLock)
        {
            _keys.Clear();
            _keyOrder.Clear();
        }

        lock (_windowsLock)
        {
            foreach (var window in _windows.Values)
            {
                window.Timer?.Dispose();
            }

            _windows.Clear();
        }

        lock (_waitersLock)
        {
            _waiters.Clear();
        }
    }

    public void Dispose()
    {
        // The token source is cancelled, not disposed: commands still finishing may read its token.
        _statuses.Writer.TryComplete();
        _stopping.Cancel();
        Reset();
    }

    private static T WithoutFlow<T>(Func<T> create)
    {
        // Background work must not capture the request's execution context (HttpContext, scoped services).
        if (ExecutionContext.IsFlowSuppressed())
        {
            return create();
        }

        using (ExecutionContext.SuppressFlow())
        {
            return create();
        }
    }

    private void PurgeKeys(DateTimeOffset now)
    {
        while (_keyOrder.TryPeek(out var oldest) && now - oldest.At >= IdempotencyWindow)
        {
            _keyOrder.Dequeue();
            if (_keys.TryGetValue(oldest.Key, out var at) && at == oldest.At)
            {
                _keys.Remove(oldest.Key);
            }
        }
    }

    private void OnWindowElapsed(object? state)
    {
        var key = ((Guid, string))state!;
        TrackedCommand? next;
        lock (_windowsLock)
        {
            if (!_windows.TryGetValue(key, out var window))
            {
                return;
            }

            next = window.Held;
            window.Held = null;
            if (next is null)
            {
                window.Timer?.Dispose();
                _windows.Remove(key);
            }
            else
            {
                // The held command is sent now and opens a new window, so a continuous stream of setters is throttled.
                window.Timer?.Change(CoalesceWindow, Timeout.InfiniteTimeSpan);
            }
        }

        if (next is not null)
        {
            Start(next);
        }
    }

    private void Start(TrackedCommand command)
    {
        Interlocked.Increment(ref _running);
        _ = WithoutFlow(() => Task.Run(() => RunAsync(command)));
    }

    private async Task RunAsync(TrackedCommand command)
    {
        var waiter = command.Definition.Confirmation == ConfirmationMode.AwaitState ? AddWaiter(command) : null;
        using var timeout = new CancellationTokenSource(command.Timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _stopping.Token);
        string status;
        string? code = null;
        string? detail = null;
        try
        {
            var outcome = await _dispatcher.DispatchAsync(command.IntegrationId, command.Batch, linked.Token).ConfigureAwait(false);
            switch (outcome)
            {
                case CommandOutcome.Rejected rejected:
                    status = CommandStatuses.Failed;
                    code = CodeFor(rejected.Code);
                    detail = rejected.Code;
                    break;
                case CommandOutcome.Accepted when waiter is not null:
                    if (await waiter.Done.Task.WaitAsync(linked.Token).ConfigureAwait(false) == ConfirmationState.Failed)
                    {
                        // The device reported a state that means it did not happen (e.g. a jammed lock).
                        status = CommandStatuses.Failed;
                        code = ErrorCodes.CommandsNotConfirmed;
                        detail = waiter.Progress.FailedWith;
                    }
                    else
                    {
                        status = CommandStatuses.Confirmed;
                    }

                    break;
                default:
                    status = CommandStatuses.Confirmed;
                    break;
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            status = CommandStatuses.TimedOut;
            code = ErrorCodes.CommandsTimedOut;
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            status = CommandStatuses.Failed;
            code = ErrorCodes.CommandsUnavailable;
            detail = "shutting_down";
        }
        catch (Exception ex)
        {
            LogDispatchFailed(command.CommandId, ex.GetType().Name, _secrets.Redact(ex.Message));
            status = CommandStatuses.Failed;
            code = ErrorCodes.CommandsFailed;
            detail = ex.GetType().Name;
        }
        finally
        {
            if (waiter is not null)
            {
                RemoveWaiter(command, waiter);
            }

            Interlocked.Decrement(ref _running);
        }

        Finish(command, status, code, detail);
    }

    private Waiter AddWaiter(TrackedCommand command)
    {
        var waiter = new Waiter(command.Confirmation.Start());
        lock (_waitersLock)
        {
            foreach (var attributeId in command.Confirmation.AttributeIds)
            {
                if (!_waiters.TryGetValue(attributeId, out var list))
                {
                    list = [];
                    _waiters[attributeId] = list;
                }

                list.Add(waiter);
            }
        }

        return waiter;
    }

    private void RemoveWaiter(TrackedCommand command, Waiter waiter)
    {
        lock (_waitersLock)
        {
            foreach (var attributeId in command.Confirmation.AttributeIds)
            {
                if (_waiters.TryGetValue(attributeId, out var list))
                {
                    list.Remove(waiter);
                    if (list.Count == 0)
                    {
                        _waiters.Remove(attributeId);
                    }
                }
            }
        }
    }

    private void Finish(TrackedCommand command, string status, string? code, string? detail)
    {
        Publish(new CommandStatusDto(command.CommandId, status, code));
        _ = WithoutFlow(() => Task.Run(() => AuditAsync(command, status, code, detail)));
    }

    /// <summary>Queues a status push; one loop sends them in order, so <c>pending</c> always arrives before the final status.</summary>
    private void Publish(CommandStatusDto status) => _statuses.Writer.TryWrite(status);

    private async Task PublishLoopAsync()
    {
        await foreach (var status in _statuses.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await _publisher.CommandStatusAsync(status, _stopping.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPublishFailed(status.CommandId, ex.GetType().Name);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task AuditAsync(TrackedCommand command, string status, string? code, string? detail)
    {
        try
        {
            var arguments = command.Batch.Commands.Count == 1 ? command.Batch.Commands[0].Arguments : default;
            await using var scope = _scopes.CreateAsyncScope();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditLog>();
            await audit.WriteAsync(
                new AuditEvent(
                    AuditActions.CommandExecuted,
                    EndpointTarget,
                    command.EndpointId.ToString("D", CultureInfo.InvariantCulture),
                    new CommandAuditDetails(
                        command.CommandId,
                        command.CapabilityId,
                        command.Definition.Name,
                        status,
                        code,
                        detail,
                        command.AuditArguments && arguments.ValueKind == JsonValueKind.Object ? arguments : null))
                {
                    Actor = command.Actor,
                },
                _stopping.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_stopping.IsCancellationRequested)
        {
            LogAuditFailed(command.CommandId, ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Command {CommandId} failed in the connector ({ExceptionType}): {Reason}")]
    private partial void LogDispatchFailed(Guid commandId, string exceptionType, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not publish the status of command {CommandId} ({ExceptionType})")]
    private partial void LogPublishFailed(Guid commandId, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not audit command {CommandId} ({ExceptionType})")]
    private partial void LogAuditFailed(Guid commandId, string exceptionType);

    private sealed class Window
    {
        public TrackedCommand? Held { get; set; }

        public ITimer? Timer { get; set; }
    }

    /// <summary>An awaitState command waiting for its confirming report (<see cref="Progress"/> and <see cref="Finished"/> under the waiters lock).</summary>
    private sealed class Waiter(CommandConfirmation.Progress progress)
    {
        public CommandConfirmation.Progress Progress { get; } = progress;

        public TaskCompletionSource<ConfirmationState> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Finished { get; set; }
    }

    /// <summary>The <c>commands.executed</c> audit details.</summary>
    private sealed record CommandAuditDetails(
        Guid CommandId,
        string Capability,
        string Command,
        string Outcome,
        string? Code,
        string? Detail,
        JsonElement? Arguments);
}

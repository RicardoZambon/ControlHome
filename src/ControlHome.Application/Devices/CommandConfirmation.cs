using System.Text.Json;
using ControlHome.Domain.Capabilities;

namespace ControlHome.Application.Devices;

/// <summary>What a state report means for a command waiting to be confirmed.</summary>
public enum ConfirmationState
{
    /// <summary>Not the target (yet): an unchanged old value, a value in between (<c>opening</c>), another attribute.</summary>
    Pending,

    /// <summary>The device reports the command's target state.</summary>
    Confirmed,

    /// <summary>The device reports a state that means the command failed (e.g. <c>jammed</c>).</summary>
    Failed,
}

/// <summary>
/// When an accepted <see cref="ConfirmationMode.AwaitState"/> command counts as done, from the state reports of its endpoint
/// capability:
/// <list type="bullet">
/// <item><b>Setters:</b> each argument bound to an attribute (<c>ArgumentDefinition.Attribute</c>, else an attribute with
/// the argument's key) must be reported with the sent value (numbers within half a step).</item>
/// <item><b>Argument-less commands</b> with <see cref="CommandDefinition.Expects"/>: its attribute must be reported with one
/// of the confirming values; a failing value (<c>jammed</c>) fails the command at once.</item>
/// <item><b>Anything else</b> (<c>toggle</c>, generic commands): the first report that changes a value of the capability.</item>
/// </list>
/// A report of the old value never confirms; one of the target value does, even unchanged (the device already was there).
/// Immutable: <see cref="Start"/> creates the progress of one command.
/// </summary>
public sealed class CommandConfirmation
{
    private readonly Dictionary<int, Target> _targets;
    private readonly bool _anyChange;

    private CommandConfirmation(IReadOnlyCollection<int> attributeIds, Dictionary<int, Target> targets, bool anyChange)
    {
        AttributeIds = attributeIds;
        _targets = targets;
        _anyChange = anyChange;
    }

    /// <summary>The attributes whose reports are looked at.</summary>
    public IReadOnlyCollection<int> AttributeIds { get; }

    /// <summary>Confirmed by the first report that changes any of <paramref name="attributeIds"/>.</summary>
    public static CommandConfirmation OnAnyChange(IReadOnlyCollection<int> attributeIds)
    {
        ArgumentNullException.ThrowIfNull(attributeIds);
        return new CommandConfirmation([.. attributeIds.Distinct()], [], anyChange: true);
    }

    /// <summary>The confirmation rule of <paramref name="command"/> sent with <paramref name="arguments"/>.</summary>
    /// <param name="attributeIds">The endpoint capability's attribute ids by key.</param>
    public static CommandConfirmation For(
        CapabilityDefinition capability,
        CommandDefinition command,
        JsonElement arguments,
        IReadOnlyDictionary<string, int> attributeIds)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(attributeIds);

        var targets = new Dictionary<int, Target>();
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var argument in command.Arguments)
            {
                var key = argument.Attribute ?? argument.Key;
                if (!arguments.TryGetProperty(argument.Key, out var sent)
                    || sent.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                    || capability.FindAttribute(key) is not { } attribute
                    || !attributeIds.TryGetValue(key, out var attributeId))
                {
                    continue;
                }

                targets[attributeId] = new Target([StateValue.FromJson(sent)], [], ToleranceOf(attribute.Schema));
            }
        }

        if (targets.Count == 0
            && command.Expects is { } expected
            && capability.FindAttribute(expected.Attribute) is { } watched
            && attributeIds.TryGetValue(expected.Attribute, out var watchedId))
        {
            targets[watchedId] = new Target(expected.Confirms, expected.Fails, ToleranceOf(watched.Schema));
        }

        return targets.Count == 0
            ? OnAnyChange([.. attributeIds.Values])
            : new CommandConfirmation([.. targets.Keys], targets, anyChange: false);
    }

    /// <summary>The progress of one command (not thread-safe: the caller serializes it).</summary>
    public Progress Start() => new(this);

    /// <summary>Half a step for stepped numbers (the device may round), else a tiny relative tolerance.</summary>
    private static double ToleranceOf(ValueSchema schema) =>
        schema is NumberSchema { Step: > 0 } number ? number.Step.Value / 2 : 1e-6;

    /// <summary>One command's progress: which targets the device has reported so far.</summary>
    public sealed class Progress
    {
        private readonly CommandConfirmation _rule;
        private readonly HashSet<int> _unmatched;

        internal Progress(CommandConfirmation rule)
        {
            _rule = rule;
            _unmatched = [.. rule._targets.Keys];
        }

        /// <summary>The reported value that failed the command (for the audit), once <see cref="ConfirmationState.Failed"/>.</summary>
        public string? FailedWith { get; private set; }

        public ConfirmationState Apply(AttributeStateChange change)
        {
            ArgumentNullException.ThrowIfNull(change);
            if (_rule._anyChange)
            {
                return change.Changed && _rule.AttributeIds.Contains(change.AttributeId) ? ConfirmationState.Confirmed : ConfirmationState.Pending;
            }

            if (!_rule._targets.TryGetValue(change.AttributeId, out var target))
            {
                return ConfirmationState.Pending;
            }

            if (target.Fails(change.Value))
            {
                FailedWith = change.Value.ToString();
                return ConfirmationState.Failed;
            }

            if (target.Confirms(change.Value))
            {
                _unmatched.Remove(change.AttributeId);
            }
            else
            {
                // Matched earlier but moved away again (e.g. one of two color arguments): wait for it again.
                _unmatched.Add(change.AttributeId);
            }

            return _unmatched.Count == 0 ? ConfirmationState.Confirmed : ConfirmationState.Pending;
        }
    }

    private sealed class Target(IReadOnlyList<StateValue> confirms, IReadOnlyList<StateValue> fails, double tolerance)
    {
        public bool Confirms(StateValue value) => confirms.Any(c => Same(c, value));

        public bool Fails(StateValue value) => fails.Any(f => Same(f, value));

        private bool Same(StateValue expected, StateValue reported) =>
            expected.Kind == StateValueKind.Number && reported.Kind == StateValueKind.Number
                ? Math.Abs(expected.N!.Value - reported.N!.Value) <= Math.Max(tolerance, 1e-6 * Math.Abs(expected.N.Value))
                : expected.Equals(reported);
    }
}

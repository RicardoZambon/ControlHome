using ControlHome.Domain.Capabilities;

namespace ControlHome.Application.Security;

/// <summary>What the safety policy says about one command from one principal.</summary>
public enum SafetyDecision
{
    /// <summary>Send it.</summary>
    Allowed,

    /// <summary>Safety-critical from a user: allowed only with an active step-up grant (PIN), consumed by this command.</summary>
    StepUpRequired,

    /// <summary>Never allowed for this principal (Viewer; an automation without <c>allowSafetyCritical</c>).</summary>
    Forbidden,
}

/// <summary>
/// The rules for safety-critical commands (<c>lock.unlock</c>, <c>doorOpener.open</c>, <c>cover.open</c> and
/// <c>cover.setPosition</c> on a gate or garage, <c>alarmPanel.disarm</c>):
/// <list type="bullet">
/// <item>fail-safe directions (<c>lock.lock</c>, <c>cover.close</c>) are never gated;</item>
/// <item>users need an Admin or Member role for any command, and a step-up grant (PIN, 60 s, one command) for a gated one;</item>
/// <item>Viewers can never send commands;</item>
/// <item>system principals (automations, scenes) may send gated commands only when an Admin allowed it.</item>
/// </list>
/// Delivery of gated commands is at-most-once (idempotency key, no retries) — enforced by the command service.
/// </summary>
public static class SafetyPolicy
{
    /// <summary>Whether <paramref name="command"/> on a device of class <paramref name="deviceClass"/> needs the safety rules.</summary>
    public static bool IsGated(CommandDefinition command, string? deviceClass)
    {
        ArgumentNullException.ThrowIfNull(command);
        return !command.FailSafe && command.IsSafetyCriticalFor(deviceClass);
    }

    public static SafetyDecision Evaluate(CommandPrincipal principal, CommandDefinition command, string? deviceClass)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(command);

        if (!principal.CanControl)
        {
            return SafetyDecision.Forbidden;
        }

        if (!IsGated(command, deviceClass))
        {
            return SafetyDecision.Allowed;
        }

        return principal.Kind switch
        {
            CommandPrincipalKind.User => SafetyDecision.StepUpRequired,
            CommandPrincipalKind.System when principal.AllowSafetyCritical => SafetyDecision.Allowed,
            _ => SafetyDecision.Forbidden,
        };
    }
}

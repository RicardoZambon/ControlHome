namespace ControlHome.Application.Security;

/// <summary>Who sends a command: a signed-in user, or the system (automations and scenes, later).</summary>
public enum CommandPrincipalKind
{
    User,
    System,
}

/// <summary>
/// The sender of a device command, as <c>DeviceCommandService</c> authorizes it (<see cref="SafetyPolicy"/>).
/// </summary>
/// <param name="UserId">The user (for <see cref="CommandPrincipalKind.User"/>).</param>
/// <param name="Name">The user name, or the automation / scene name.</param>
/// <param name="Roles">The user's roles (empty for the system).</param>
/// <param name="AllowSafetyCritical">
/// System principals only: an Admin allowed this automation or scene to send safety-critical commands (audited).
/// </param>
public sealed record CommandPrincipal(
    CommandPrincipalKind Kind,
    Guid? UserId,
    string? Name,
    IReadOnlyList<string> Roles,
    bool AllowSafetyCritical)
{
    public static CommandPrincipal User(Guid userId, string? userName, IReadOnlyList<string> roles) =>
        new(CommandPrincipalKind.User, userId, userName, roles, AllowSafetyCritical: false);

    public static CommandPrincipal System(string name, bool allowSafetyCritical = false) =>
        new(CommandPrincipalKind.System, null, name, [], allowSafetyCritical);

    /// <summary>A user who may control devices (Admin or Member).</summary>
    public bool CanControl =>
        Kind == CommandPrincipalKind.System
        || Roles.Contains(Domain.Security.Roles.Admin, StringComparer.Ordinal)
        || Roles.Contains(Domain.Security.Roles.Member, StringComparer.Ordinal);
}

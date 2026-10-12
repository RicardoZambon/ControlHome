namespace ControlHome.Domain.Auditing;

/// <summary>An append-only record of a security-relevant action (setup, logins, user and configuration changes).</summary>
public sealed class AuditLogEntry
{
    public long Id { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }

    public AuditActorType ActorType { get; set; }

    public string? ActorId { get; set; }

    public string? ActorName { get; set; }

    /// <summary>Dotted action name, e.g. <c>auth.login.succeeded</c>.</summary>
    public string Action { get; set; } = string.Empty;

    public string? TargetType { get; set; }

    public string? TargetId { get; set; }

    /// <summary>Optional JSON object with action details (never secrets).</summary>
    public string? DetailsJson { get; set; }

    public string? IpAddress { get; set; }
}

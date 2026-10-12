namespace ControlHome.Domain.Auditing;

/// <summary>Who performed an audited action.</summary>
public enum AuditActorType
{
    /// <summary>A signed-in user.</summary>
    User,

    /// <summary>The application itself (background jobs, startup).</summary>
    System,

    /// <summary>An unauthenticated caller (e.g. a failed login).</summary>
    Anonymous,
}

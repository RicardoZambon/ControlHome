namespace ControlHome.Application.Auditing;

/// <summary>Writes audit entries; the actor and client IP come from <see cref="Abstractions.ICurrentActor"/>.</summary>
public interface IAuditLog
{
    /// <summary>Adds the entry and saves it (together with any pending changes of the shared DbContext).</summary>
    Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken);
}

/// <summary>
/// An audited action. <paramref name="Action"/> is dotted (see <see cref="AuditActions"/>); <paramref name="Details"/> is
/// serialized to JSON and must never contain secrets (field names only).
/// </summary>
public sealed record AuditEvent(string Action, string? TargetType = null, string? TargetId = null, object? Details = null)
{
    /// <summary>
    /// Who acted, when the entry is written outside the request that caused it (e.g. a command's outcome, audited after
    /// the device answered); <c>null</c> uses the current <see cref="Abstractions.ICurrentActor"/>.
    /// </summary>
    public AuditActorSnapshot? Actor { get; init; }
}

/// <summary>A copy of an <see cref="Abstractions.ICurrentActor"/> taken while the request is still running.</summary>
public sealed record AuditActorSnapshot(Domain.Auditing.AuditActorType Type, Guid? UserId, string? UserName, string? IpAddress)
{
    public static AuditActorSnapshot From(Abstractions.ICurrentActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new AuditActorSnapshot(actor.Type, actor.UserId, actor.UserName, actor.IpAddress);
    }
}

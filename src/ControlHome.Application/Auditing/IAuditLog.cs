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
public sealed record AuditEvent(string Action, string? TargetType = null, string? TargetId = null, object? Details = null);

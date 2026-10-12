using System.Globalization;
using System.Text.Json;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Auditing;
using ControlHome.Domain.Auditing;

namespace ControlHome.Infrastructure.Auditing;

/// <summary>Writes <see cref="AuditLogEntry"/> rows; actor and IP come from <see cref="ICurrentActor"/>.</summary>
public sealed class AuditLog(IControlHomeDb db, ICurrentActor actor, TimeProvider timeProvider) : IAuditLog
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        db.AuditLog.Add(new AuditLogEntry
        {
            OccurredAtUtc = timeProvider.GetUtcNow(),
            ActorType = actor.Type,
            ActorId = actor.UserId?.ToString("D", CultureInfo.InvariantCulture),
            ActorName = actor.UserName,
            Action = auditEvent.Action,
            TargetType = auditEvent.TargetType,
            TargetId = auditEvent.TargetId,
            DetailsJson = auditEvent.Details is null ? null : JsonSerializer.Serialize(auditEvent.Details, auditEvent.Details.GetType(), _json),
            IpAddress = actor.IpAddress,
        });

        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>The actor outside any request (background jobs, startup): <see cref="AuditActorType.System"/>.</summary>
public sealed class SystemActor : ICurrentActor
{
    public static SystemActor Instance { get; } = new();

    public AuditActorType Type => AuditActorType.System;

    public Guid? UserId => null;

    public string? UserName => null;

    public string? IpAddress => null;
}

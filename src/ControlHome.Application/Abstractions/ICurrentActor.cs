using ControlHome.Domain.Auditing;

namespace ControlHome.Application.Abstractions;

/// <summary>Who is performing the current operation (the Api implements it from the HTTP request).</summary>
public interface ICurrentActor
{
    AuditActorType Type { get; }

    /// <summary>The user id when <see cref="Type"/> is <see cref="AuditActorType.User"/>.</summary>
    Guid? UserId { get; }

    /// <summary>The user name when known.</summary>
    string? UserName { get; }

    /// <summary>The client IP address when the operation comes from a request.</summary>
    string? IpAddress { get; }
}

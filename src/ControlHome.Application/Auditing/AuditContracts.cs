namespace ControlHome.Application.Auditing;

/// <summary>One audit log entry, newest first in listings.</summary>
/// <param name="ActorType"><c>User</c>, <c>System</c> or <c>Anonymous</c>.</param>
/// <param name="DetailsJson">Optional JSON object with action details.</param>
public sealed record AuditEntryDto(
    long Id,
    DateTimeOffset OccurredAtUtc,
    string ActorType,
    string? ActorId,
    string? ActorName,
    string Action,
    string? TargetType,
    string? TargetId,
    string? DetailsJson,
    string? IpAddress);

/// <summary>A page of items; pass <paramref name="NextCursor"/> back as <c>cursor</c> to get the next page (null = last page).</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, long? NextCursor);

using ControlHome.Application.Common;

namespace ControlHome.Application.HomeBackup;

/// <summary>The automatic backups (<c>GET /api/backup/scheduled</c>, Admin): the schedule, how it went, the files kept.</summary>
/// <param name="Enabled">Backups are written on a schedule (off: only "back up now" writes one).</param>
/// <param name="IntervalHours">How often one is written (24: every day).</param>
/// <param name="Keep">How many are kept; older ones are deleted.</param>
/// <param name="LastFailedAtUtc">When the last attempt failed, if it did and none succeeded since (show a warning).</param>
/// <param name="Files">The backups kept, newest first.</param>
public sealed record ScheduledBackupsDto(
    bool Enabled,
    double IntervalHours,
    int Keep,
    DateTimeOffset? LastFailedAtUtc,
    IReadOnlyList<ScheduledBackupDto> Files);

/// <summary>One automatic backup (a zip without credentials): download it with <c>GET /api/backup/scheduled/{name}</c>.</summary>
public sealed record ScheduledBackupDto(string Name, DateTimeOffset WrittenAtUtc, long SizeBytes);

/// <summary>The automatic backups in <c>{DataPath}/backups</c> (the Infrastructure scheduler; none without a data folder).</summary>
public interface IScheduledBackups
{
    ScheduledBackupsDto Describe();

    /// <summary>Writes a backup now, whatever the schedule. Errors: 503 <c>backup.write_failed</c> (the folder, the disk).</summary>
    Task<Result<ScheduledBackupDto>> RunNowAsync(CancellationToken cancellationToken);

    /// <summary>A kept backup's content by its exact name, or <c>null</c> when no kept backup has that name.</summary>
    Task<byte[]?> ReadAsync(string name, CancellationToken cancellationToken);
}

/// <summary>Hosts without the scheduler (build-time OpenAPI generation): no automatic backups.</summary>
public sealed class NoScheduledBackups : IScheduledBackups
{
    public static NoScheduledBackups Instance { get; } = new();

    public ScheduledBackupsDto Describe() => new(false, 24, 0, null, []);

    public Task<Result<ScheduledBackupDto>> RunNowAsync(CancellationToken cancellationToken) =>
        Task.FromResult<Result<ScheduledBackupDto>>(
            AppError.Unavailable(ErrorCodes.BackupWriteFailed, "This server does not write backups to a data folder."));

    public Task<byte[]?> ReadAsync(string name, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
}

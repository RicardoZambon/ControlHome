using System.Globalization;
using ControlHome.Application.Abstractions;
using ControlHome.Application.HomeBackup;
using ControlHome.Application.Runtime;
using ControlHome.Infrastructure.Configuration;
using ControlHome.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlHome.Infrastructure.HomeBackup;

/// <summary>The scheduled backups (configuration section <c>Backup</c>).</summary>
public sealed class BackupOptions
{
    public const string SectionName = "Backup";

    /// <summary>Whether backups are written on a schedule.</summary>
    public bool ScheduleEnabled { get; set; } = true;

    /// <summary>How often a backup is written (the newest one in the folder counts, also across restarts).</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>How many scheduled backups are kept (older ones are deleted).</summary>
    public int Keep { get; set; } = 7;

    /// <summary>How often the schedule is checked.</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Writes a backup (without secrets: no passphrase is stored) to <c>{DataPath}/backups</c> once per
/// <see cref="BackupOptions.Interval"/> and keeps the newest <see cref="BackupOptions.Keep"/> — only on the active runtime
/// (a standby sharing the database does not double them) and only after the first-run setup. Files are written atomically
/// (temporary file, then renamed) and are owner-only on Linux like the rest of the data folder.
/// </summary>
public sealed partial class BackupScheduler(
    DatabaseReadiness readiness,
    IRuntimeStatus status,
    IServiceScopeFactory scopes,
    DataDirectory dataDirectory,
    TimeProvider timeProvider,
    IOptions<BackupOptions> options,
    ILogger<BackupScheduler> logger) : BackgroundService
{
    public const string Folder = "backups";
    public const string FilePrefix = "controlhome-backup-";
    private const string TimestampFormat = "yyyyMMdd-HHmmss";

    /// <summary>
    /// Writes a backup when one is due (or always with <paramref name="force"/>) and prunes old ones; returns the new file's
    /// path, or <c>null</c> when nothing was due.
    /// </summary>
    public async Task<string?> RunOnceAsync(bool force, CancellationToken cancellationToken)
    {
        if (!force && status.Mode != RuntimeMode.Active)
        {
            return null;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IControlHomeDb>();
        if (!await db.Homes.AnyAsync(h => h.SetupCompletedAtUtc != null, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        if (!force && Existing().FirstOrDefault() is { } newest && now - newest.WrittenAtUtc < options.Value.Interval)
        {
            return null;
        }

        var archive = await scope.ServiceProvider.GetRequiredService<BackupService>()
            .ExportAsync(passphrase: null, audit: false, cancellationToken)
            .ConfigureAwait(false);
        var folder = dataDirectory.EnsureSubdirectory(Folder);
        var name = string.Create(CultureInfo.InvariantCulture, $"{FilePrefix}{now.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture)}.zip");
        var path = Path.Combine(folder, name);
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, archive.Content, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
        LogWritten(name, archive.Content.Length);

        foreach (var old in Existing().Skip(Math.Max(1, options.Value.Keep)))
        {
            try
            {
                File.Delete(old.Path);
            }
            catch (IOException ex)
            {
                LogPruneFailed(Path.GetFileName(old.Path), ex.GetType().Name);
            }
        }

        return path;
    }

    /// <summary>The scheduled backups in the folder, newest first (by the time in their name).</summary>
    public IReadOnlyList<(string Path, DateTimeOffset WrittenAtUtc)> Existing()
    {
        var folder = dataDirectory.GetPath(Folder);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.GetFiles(folder, $"{FilePrefix}*.zip")
            .Select(path => (Path: path, Stamp: Path.GetFileNameWithoutExtension(path)[FilePrefix.Length..]))
            .Where(f => DateTime.TryParseExact(f.Stamp, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _))
            .Select(f => (f.Path, new DateTimeOffset(DateTime.ParseExact(f.Stamp, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal), TimeSpan.Zero)))
            .OrderByDescending(f => f.Item2)
            .ToList();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.ScheduleEnabled)
        {
            return;
        }

        try
        {
            await readiness.WaitUntilReadyAsync(stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(force: false, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    LogFailed(ex.GetType().Name);
                }

                await Task.Delay(options.Value.CheckInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled backup written: {FileName} ({Bytes} bytes)")]
    private partial void LogWritten(string fileName, int bytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The scheduled backup failed ({ExceptionType}); retrying at the next check")]
    private partial void LogFailed(string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting the old backup {FileName} failed ({ExceptionType})")]
    private partial void LogPruneFailed(string fileName, string exceptionType);
}

public static class BackupScheduleServiceCollectionExtensions
{
    /// <summary>The daily backup to <c>{DataPath}/backups</c> (not during build-time OpenAPI generation).</summary>
    public static IServiceCollection AddBackupSchedule(this IServiceCollection services)
    {
        services.AddOptions<BackupOptions>().BindConfiguration(BackupOptions.SectionName);
        services.TryAddSingleton<BackupScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<BackupScheduler>());
        return services;
    }
}

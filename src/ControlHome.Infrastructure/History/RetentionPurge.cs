using System.Data;
using ControlHome.Application.Abstractions;
using ControlHome.Domain.History;
using ControlHome.Infrastructure.Devices;
using ControlHome.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlHome.Infrastructure.History;

/// <summary>
/// A table the retention job purges. The built-in targets cover history and the audit log; later tables register their own
/// (AutomationRun: 30 days or the last 200; Notification: 90 days) with <c>services.TryAddEnumerable</c>.
/// </summary>
public interface IRetentionPurgeTarget
{
    /// <summary>For logs.</summary>
    string Name { get; }

    /// <summary>Deletes what <paramref name="settings"/> no longer keeps, at most <see cref="HistoryOptions.PurgeBatchSize"/> rows per statement.</summary>
    /// <returns>The rows deleted.</returns>
    Task<long> PurgeAsync(RetentionSettings settings, CancellationToken cancellationToken);
}

/// <summary>
/// The retention job (hourly, on the active runtime): reads <see cref="RetentionSettings"/> (defaults until saved) and runs
/// every <see cref="IRetentionPurgeTarget"/>; one failing target does not stop the others.
/// </summary>
public sealed partial class RetentionPurge(
    IServiceScopeFactory scopes,
    IEnumerable<IRetentionPurgeTarget> targets,
    ILogger<RetentionPurge> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Rows deleted per target.</summary>
    public async Task<IReadOnlyDictionary<string, long>> PurgeOnceAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RetentionSettings settings;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<IControlHomeDb>();
                settings = await db.RetentionSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                    ?? new RetentionSettings();
            }

            var deleted = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var target in targets)
            {
                try
                {
                    deleted[target.Name] = await target.PurgeAsync(settings, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    LogTargetFailed(target.Name, ex.GetType().Name);
                }
            }

            if (deleted.Values.Any(d => d > 0))
            {
                LogPurged(string.Join(", ", deleted.Where(d => d.Value > 0).Select(d => $"{d.Key} {d.Value}")));
            }

            return deleted;
        }
        finally
        {
            _gate.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Purging {Target} failed ({ExceptionType}); retrying with the next run")]
    private partial void LogTargetFailed(string target, string exceptionType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Retention purge deleted rows: {Deleted}")]
    private partial void LogPurged(string deleted);
}

/// <summary>Shared plumbing of the built-in targets: <c>DELETE TOP (n)</c> loops on short-lived statements.</summary>
internal abstract class SqlPurgeTarget(IDatabaseConnectionProvider connections, IOptions<HistoryOptions> options) : IRetentionPurgeTarget
{
    public abstract string Name { get; }

    protected HistoryOptions Options => options.Value;

    public async Task<long> PurgeAsync(RetentionSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var connectionString = connections.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection is configured.");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await PurgeAsync(connection, settings, cancellationToken).ConfigureAwait(false);
    }

    protected abstract Task<long> PurgeAsync(SqlConnection connection, RetentionSettings settings, CancellationToken cancellationToken);

    /// <summary>Runs <paramref name="deleteTop"/> (a <c>DELETE TOP (@batch)</c>) until it deletes fewer rows than a batch.</summary>
    protected async Task<long> DeleteInBatchesAsync(
        SqlConnection connection,
        string deleteTop,
        Action<SqlParameterCollection> parameters,
        CancellationToken cancellationToken)
    {
        var batch = Math.Max(1, Options.PurgeBatchSize);
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = HistorySql.Command(connection, null, deleteTop, Options.CommandTimeout);
            command.Parameters.Add(new SqlParameter("@batch", SqlDbType.Int) { Value = batch });
            parameters(command.Parameters);
            var deleted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            total += deleted;
            if (deleted < batch)
            {
                return total;
            }
        }
    }

    /// <summary>The cutoff no older than any of the watermarks (<c>null</c> when a watermark does not exist yet: nothing is final).</summary>
    protected async Task<DateTimeOffset?> CapByWatermarksAsync(
        SqlConnection connection,
        DateTimeOffset cutoff,
        IEnumerable<string> watermarks,
        CancellationToken cancellationToken)
    {
        foreach (var name in watermarks)
        {
            if (await HistorySql.GetWatermarkAsync(connection, null, name, Options.CommandTimeout, cancellationToken).ConfigureAwait(false) is not { } watermark)
            {
                return null;
            }

            if (watermark < cutoff)
            {
                cutoff = watermark;
            }
        }

        return cutoff;
    }
}

/// <summary>
/// <c>StateLog</c> rows older than the retention and than both rollups reading it (5-minute statistics, energy). Per
/// attribute the last row before the cutoff stays (it is the value carried into later windows) unless the attribute is gone.
/// </summary>
internal sealed class StateLogPurgeTarget(IDatabaseConnectionProvider connections, RuntimeClock clock, IOptions<HistoryOptions> options)
    : SqlPurgeTarget(connections, options)
{
    private const string DeleteTop = """
        DELETE TOP (@batch) s
        FROM [ch].[StateLog] AS s
        WHERE s.TsUtc < @cutoff
          AND (NOT EXISTS (SELECT 1 FROM [ch].[DeviceAttributes] AS a WHERE a.Id = s.AttributeId)
               OR EXISTS (SELECT 1 FROM [ch].[StateLog] AS n
                          WHERE n.AttributeId = s.AttributeId AND n.TsUtc > s.TsUtc AND n.TsUtc <= @cutoff));
        """;

    public override string Name => "StateLog";

    protected override async Task<long> PurgeAsync(SqlConnection connection, RetentionSettings settings, CancellationToken cancellationToken)
    {
        var retention = clock.Time.GetUtcNow() - TimeSpan.FromDays(settings.StateLogDays);
        if (await CapByWatermarksAsync(connection, retention, [RollupProgress.ShortTerm, RollupProgress.Energy], cancellationToken)
                .ConfigureAwait(false) is not { } cutoff)
        {
            return 0;
        }

        return await DeleteInBatchesAsync(connection, DeleteTop, p => p.Add(HistorySql.Time("@cutoff", cutoff)), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary><c>StatShortTerm</c> rows older than the retention and than the hourly rollup reading them.</summary>
internal sealed class ShortTermPurgeTarget(IDatabaseConnectionProvider connections, RuntimeClock clock, IOptions<HistoryOptions> options)
    : SqlPurgeTarget(connections, options)
{
    private const string DeleteTop = "DELETE TOP (@batch) FROM [ch].[StatShortTerm] WHERE BucketStartUtc < @cutoff;";

    public override string Name => "StatShortTerm";

    protected override async Task<long> PurgeAsync(SqlConnection connection, RetentionSettings settings, CancellationToken cancellationToken)
    {
        var retention = clock.Time.GetUtcNow() - TimeSpan.FromDays(settings.ShortTermStatsDays);
        if (await CapByWatermarksAsync(connection, retention, [RollupProgress.Hourly], cancellationToken).ConfigureAwait(false) is not { } cutoff)
        {
            return 0;
        }

        return await DeleteInBatchesAsync(connection, DeleteTop, p => p.Add(HistorySql.Time("@cutoff", cutoff)), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary><c>DeviceEvent</c> rows older than the retention.</summary>
internal sealed class DeviceEventPurgeTarget(IDatabaseConnectionProvider connections, RuntimeClock clock, IOptions<HistoryOptions> options)
    : SqlPurgeTarget(connections, options)
{
    private const string DeleteTop = "DELETE TOP (@batch) FROM [ch].[DeviceEvent] WHERE TsUtc < @cutoff;";

    public override string Name => "DeviceEvent";

    protected override Task<long> PurgeAsync(SqlConnection connection, RetentionSettings settings, CancellationToken cancellationToken)
    {
        var cutoff = clock.Time.GetUtcNow() - TimeSpan.FromDays(settings.DeviceEventDays);
        return DeleteInBatchesAsync(connection, DeleteTop, p => p.Add(HistorySql.Time("@cutoff", cutoff)), cancellationToken);
    }
}

/// <summary>Audit entries older than the retention (by the app clock, which stamps them).</summary>
internal sealed class AuditLogPurgeTarget(IDatabaseConnectionProvider connections, TimeProvider timeProvider, IOptions<HistoryOptions> options)
    : SqlPurgeTarget(connections, options)
{
    private const string DeleteTop = "DELETE TOP (@batch) FROM [ch].[AuditLog] WHERE OccurredAtUtc < @cutoff;";

    public override string Name => "AuditLog";

    protected override Task<long> PurgeAsync(SqlConnection connection, RetentionSettings settings, CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow() - TimeSpan.FromDays(settings.AuditLogDays);
        return DeleteInBatchesAsync(
            connection,
            DeleteTop,
            p => p.Add(new SqlParameter("@cutoff", SqlDbType.DateTimeOffset) { Value = cutoff }),
            cancellationToken);
    }
}

/// <summary>
/// History kept forever whose attribute or endpoint is gone (its integration was deleted): <c>StatHourly</c>,
/// <c>EnergyHourly</c> and <c>EnergySourceSelection</c>. The ids still present are found with one index seek each (a skip
/// scan), never a full scan of the history.
/// </summary>
internal sealed class OrphanedHistoryPurgeTarget(IDatabaseConnectionProvider connections, IOptions<HistoryOptions> options)
    : SqlPurgeTarget(connections, options)
{
    public override string Name => "Orphans";

    protected override async Task<long> PurgeAsync(SqlConnection connection, RetentionSettings settings, CancellationToken cancellationToken)
    {
        long total = 0;
        foreach (var attributeId in await OrphansAsync<int>(connection, "[ch].[StatHourly]", "AttributeId", "[ch].[DeviceAttributes]", SqlDbType.Int, cancellationToken)
                     .ConfigureAwait(false))
        {
            total += await DeleteInBatchesAsync(
                    connection,
                    "DELETE TOP (@batch) FROM [ch].[StatHourly] WHERE AttributeId = @id;",
                    p => p.Add(new SqlParameter("@id", SqlDbType.Int) { Value = attributeId }),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var endpointId in await OrphansAsync<Guid>(connection, "[ch].[EnergyHourly]", "EndpointId", "[ch].[Endpoints]", SqlDbType.UniqueIdentifier, cancellationToken)
                     .ConfigureAwait(false))
        {
            total += await DeleteInBatchesAsync(
                    connection,
                    "DELETE TOP (@batch) FROM [ch].[EnergyHourly] WHERE EndpointId = @id;",
                    p => p.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = endpointId }),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        total += await DeleteInBatchesAsync(
                connection,
                "DELETE TOP (@batch) s FROM [ch].[EnergySourceSelection] AS s WHERE NOT EXISTS (SELECT 1 FROM [ch].[Endpoints] AS e WHERE e.Id = s.EndpointId);",
                _ => { },
                cancellationToken)
            .ConfigureAwait(false);
        return total;
    }

    /// <summary>The distinct <paramref name="column"/> values of <paramref name="table"/> (its leading key) missing from <paramref name="parent"/>.</summary>
    private async Task<List<T>> OrphansAsync<T>(
        SqlConnection connection,
        string table,
        string column,
        string parent,
        SqlDbType type,
        CancellationToken cancellationToken)
        where T : struct
    {
        // Two statements rather than "@after IS NULL OR …", so each one is a plain seek on the leading key.
        var first = $"SELECT TOP (1) {column} FROM {table} ORDER BY {column};";
        var next = $"SELECT TOP (1) {column} FROM {table} WHERE {column} > @after ORDER BY {column};";
        var exists = $"SELECT CASE WHEN EXISTS (SELECT 1 FROM {parent} WHERE Id = @id) THEN 1 ELSE 0 END;";
        var orphans = new List<T>();
        object? after = null;
        while (true)
        {
            await using var command = HistorySql.Command(connection, null, after is null ? first : next, Options.CommandTimeout);
            if (after is not null)
            {
                command.Parameters.Add(new SqlParameter("@after", type) { Value = after });
            }

            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not T id)
            {
                return orphans;
            }

            await using var check = HistorySql.Command(connection, null, exists, Options.CommandTimeout);
            check.Parameters.Add(new SqlParameter("@id", type) { Value = id });
            if (Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 0)
            {
                orphans.Add(id);
            }

            after = id;
        }
    }
}

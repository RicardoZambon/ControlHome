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

/// <summary>History rows still on their way to <c>StateLog</c> (the write-behind): rollups never close a bucket they belong to.</summary>
public interface IPendingHistory
{
    /// <summary>The oldest observation among the state history rows not yet written, or <c>null</c>.</summary>
    DateTimeOffset? OldestPendingHistoryUtc { get; }
}

/// <summary>What one <see cref="HistoryRollup.RunOnceAsync"/> did.</summary>
/// <param name="ShortTermBatches">5-minute windows computed (each in its own transaction).</param>
public sealed record RollupRunResult(
    int ShortTermBatches,
    int HourlyBatches,
    int EnergyBatches,
    DateTimeOffset? ShortTermWatermark,
    DateTimeOffset? HourlyWatermark,
    DateTimeOffset? EnergyWatermark);

/// <summary>
/// The history rollups, resumed from their <see cref="RollupProgress"/> watermarks and idempotent (each window is
/// replaced with one <c>MERGE</c> and its watermark moved in the same transaction):
/// <list type="number">
/// <item><c>StateLog</c> → <c>StatShortTerm</c> (5-minute buckets: time-weighted mean/min/max of measurements, increases of
/// counters with reset detection — see <see cref="RollupMath"/>), up to the last bucket that ended
/// <see cref="HistoryOptions.SettleDelay"/> ago and before the oldest history row the write-behind still holds;</item>
/// <item><c>StatShortTerm</c> → <c>StatHourly</c> (mean of means, min of mins, max of maxes, sum of sums), up to the
/// 5-minute watermark;</item>
/// <item><c>EnergyHourly</c> per endpoint from its <see cref="EnergySourceSelection"/>: the hourly increase of its energy
/// counter, or its power readings integrated (<see cref="RollupMath.IntegratePower"/>, <c>Estimated</c>), up to the
/// hourly watermark. Endpoints whose source is <see cref="EnergySource.VendorWindow"/> are left alone: their rows come from
/// <see cref="VendorEnergyWriter"/> (which runs under the same gate, <see cref="RunExclusiveAsync{T}"/>).</item>
/// </list>
/// A watermark starts at the oldest <c>StateLog</c> row. After downtime each stage catches up window by window
/// (<see cref="HistoryOptions.ShortTermBatch"/>, …), so no statement or transaction grows with the gap. Runs never overlap.
/// </summary>
public sealed partial class HistoryRollup(
    IServiceScopeFactory scopes,
    IDatabaseConnectionProvider connections,
    RuntimeClock clock,
    IPendingHistory pendingHistory,
    IOptions<HistoryOptions> options,
    ILogger<HistoryRollup> logger)
{
    private static readonly TimeSpan _shortTerm = ShortTermStatistic.BucketLength;
    private static readonly TimeSpan _hour = HourlyStatistic.BucketLength;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private HistoryOptions Options => options.Value;

    /// <summary>
    /// Runs <paramref name="work"/> while no rollup runs (and no other exclusive work): vendor energy windows
    /// (<see cref="VendorEnergyWriter"/>) change <c>EnergyHourly</c> and the energy source selections too.
    /// </summary>
    internal async Task<T> RunExclusiveAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs every stage until it has caught up (or <paramref name="cancellationToken"/> fires).</summary>
    public async Task<RollupRunResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = clock.Time.GetUtcNow();
            HistoryCatalog catalog;
            IReadOnlyList<EnergySourceSelection> selections;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<IControlHomeDb>();
                catalog = await HistoryCatalog.LoadAsync(db, cancellationToken).ConfigureAwait(false);

                // An endpoint has vendor energy windows once a connector reported some for it (the writer then selected
                // them): the automatic choice keeps them unless the endpoint has an energy counter.
                var withWindows = (await db.EnergySourceSelections.AsNoTracking()
                        .Where(s => s.Source == EnergySource.VendorWindow)
                        .Select(s => s.EndpointId)
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false))
                    .ToHashSet();
                selections = await catalog.SyncEnergySourcesAsync(db, withWindows.Contains, now, cancellationToken).ConfigureAwait(false);
            }

            var connectionString = connections.GetConnectionString()
                ?? throw new InvalidOperationException("No database connection is configured.");
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var limit = now - Options.SettleDelay;
            if (pendingHistory.OldestPendingHistoryUtc is { } pending && pending < limit)
            {
                limit = pending;
            }

            var (shortTermBatches, shortTerm) = await RollShortTermAsync(connection, catalog, RollupMath.Floor(limit, _shortTerm), cancellationToken)
                .ConfigureAwait(false);
            var (hourlyBatches, hourly) = shortTerm is { } shortTermDone
                ? await RollHourlyAsync(connection, RollupMath.Floor(shortTermDone, _hour), cancellationToken).ConfigureAwait(false)
                : (0, await HistorySql.GetWatermarkAsync(connection, null, RollupProgress.Hourly, Options.CommandTimeout, cancellationToken).ConfigureAwait(false));
            var activeEndpoints = catalog.Attributes.Select(a => a.EndpointId).ToHashSet();
            var (energyBatches, energy) = hourly is { } hourlyDone
                ? await RollEnergyAsync(connection, [.. selections.Where(s => activeEndpoints.Contains(s.EndpointId))], hourlyDone, cancellationToken)
                    .ConfigureAwait(false)
                : (0, await HistorySql.GetWatermarkAsync(connection, null, RollupProgress.Energy, Options.CommandTimeout, cancellationToken).ConfigureAwait(false));

            if (shortTermBatches + hourlyBatches + energyBatches > 0)
            {
                LogRolledUp(shortTermBatches, hourlyBatches, energyBatches, shortTerm, hourly, energy);
            }

            return new RollupRunResult(shortTermBatches, hourlyBatches, energyBatches, shortTerm, hourly, energy);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(int Batches, DateTimeOffset? Watermark)> RollShortTermAsync(
        SqlConnection connection,
        HistoryCatalog catalog,
        DateTimeOffset target,
        CancellationToken cancellationToken)
    {
        var attributes = catalog.WithStatistics.ToList();
        var counters = attributes.Where(a => a.Kind != StatisticKind.Measurement).Select(a => a.AttributeId).ToHashSet();
        var ids = attributes.Select(a => a.AttributeId).ToList();
        var kinds = attributes.ToDictionary(a => a.AttributeId, a => a.Kind);

        return await RollAsync(
                connection,
                RollupProgress.ShortTerm,
                _shortTerm,
                Options.ShortTermBatch,
                target,
                async (transaction, start, end) =>
                {
                    var readings = await HistorySql.ReadReadingsAsync(
                            connection,
                            transaction,
                            [.. attributes.Select(a => (a.AttributeId, counters.Contains(a.AttributeId)))],
                            start,
                            end,
                            withAfter: false,
                            Options.CommandTimeout,
                            cancellationToken)
                        .ConfigureAwait(false);
                    // A counter's baseline: the previous bucket's last counter (it remembers a dip's higher reading), else
                    // its last known reading.
                    var lastCounters = counters.Count == 0
                        ? []
                        : (await HistorySql.ReadStatisticsAsync(
                                    connection,
                                    transaction,
                                    HistorySql.ShortTermTable,
                                    counters,
                                    start - _shortTerm,
                                    start,
                                    Options.CommandTimeout,
                                    cancellationToken)
                                .ConfigureAwait(false))
                            .Where(r => r.LastCounter is not null)
                            .ToDictionary(r => r.AttributeId, r => r.LastCounter!.Value);

                    var rows = new List<StatisticRow>();
                    foreach (var (attributeId, window) in readings)
                    {
                        if (kinds[attributeId] == StatisticKind.Measurement)
                        {
                            rows.AddRange(RollupMath.Measurements(window.Before, window.Within, start, end, _shortTerm)
                                .Select(m => new StatisticRow(attributeId, m.BucketStartUtc, m.Mean, m.Min, m.Max, null, null)));
                        }
                        else
                        {
                            var baseline = lastCounters.TryGetValue(attributeId, out var last) ? last : window.Before?.Value;
                            var kind = kinds[attributeId] == StatisticKind.Total ? CounterKind.Total : CounterKind.TotalIncreasing;
                            rows.AddRange(RollupMath.Counters(baseline, window.Within, start, end, _shortTerm, kind)
                                .Select(c => new StatisticRow(attributeId, c.BucketStartUtc, null, null, null, c.Sum, c.LastCounter)));
                        }
                    }

                    await HistorySql.MergeStatisticsAsync(
                            connection,
                            transaction,
                            HistorySql.ShortTermTable,
                            ids,
                            rows,
                            start,
                            end,
                            Options.CommandTimeout,
                            cancellationToken)
                        .ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<(int Batches, DateTimeOffset? Watermark)> RollHourlyAsync(
        SqlConnection connection,
        DateTimeOffset target,
        CancellationToken cancellationToken) =>
        await RollAsync(
                connection,
                RollupProgress.Hourly,
                _hour,
                Options.HourlyBatch,
                target,
                async (transaction, start, end) =>
                {
                    var parts = await HistorySql.ReadStatisticsAsync(
                            connection,
                            transaction,
                            HistorySql.ShortTermTable,
                            null,
                            start,
                            end,
                            Options.CommandTimeout,
                            cancellationToken)
                        .ConfigureAwait(false);
                    var rows = parts
                        .Where(p => p.Mean is not null || p.Sum is not null)
                        .GroupBy(p => (p.AttributeId, Hour: RollupMath.Floor(p.BucketStartUtc, _hour)))
                        .Select(g => Combine(g.Key.AttributeId, g.Key.Hour, [.. g]))
                        .ToList();
                    await HistorySql.MergeStatisticsAsync(
                            connection,
                            transaction,
                            HistorySql.HourlyTable,
                            [.. parts.Select(p => p.AttributeId).Distinct()],
                            rows,
                            start,
                            end,
                            Options.CommandTimeout,
                            cancellationToken)
                        .ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<(int Batches, DateTimeOffset? Watermark)> RollEnergyAsync(
        SqlConnection connection,
        IReadOnlyList<EnergySourceSelection> selections,
        DateTimeOffset target,
        CancellationToken cancellationToken)
    {
        var counters = selections.Where(s => s.Source == EnergySource.Counter && s.AttributeId is not null).ToList();
        var power = selections.Where(s => s.Source == EnergySource.IntegratedPower && s.AttributeId is not null).ToList();
        List<Guid> endpoints = [.. counters.Concat(power).Select(s => s.EndpointId)];

        return await RollAsync(
                connection,
                RollupProgress.Energy,
                _hour,
                Options.EnergyBatch,
                target,
                async (transaction, start, end) =>
                {
                    var rows = new List<EnergyRow>();
                    if (counters.Count > 0)
                    {
                        var sums = await HistorySql.ReadStatisticsAsync(
                                connection,
                                transaction,
                                HistorySql.HourlyTable,
                                [.. counters.Select(c => c.AttributeId!.Value)],
                                start,
                                end,
                                Options.CommandTimeout,
                                cancellationToken)
                            .ConfigureAwait(false);
                        var byAttribute = sums.Where(s => s.Sum is not null).ToLookup(s => s.AttributeId);
                        foreach (var counter in counters)
                        {
                            rows.AddRange(byAttribute[counter.AttributeId!.Value]
                                .Select(s => new EnergyRow(counter.EndpointId, s.BucketStartUtc, s.Sum!.Value, EnergySource.Counter, Estimated: false)));
                        }
                    }

                    if (power.Count > 0)
                    {
                        var readings = await HistorySql.ReadReadingsAsync(
                                connection,
                                transaction,
                                [.. power.Select(p => (p.AttributeId!.Value, false)).Distinct()],
                                start,
                                end,
                                withAfter: true,
                                Options.CommandTimeout,
                                cancellationToken)
                            .ConfigureAwait(false);
                        foreach (var selection in power)
                        {
                            var window = readings[selection.AttributeId!.Value];
                            // Readings near double.MaxValue (a device's "invalid" sentinel) integrate to infinity: such an hour
                            // gets no row rather than failing the window on every run.
                            rows.AddRange(RollupMath.IntegratePower(window.Before, window.Within, window.After, start, end, _hour, Options.PowerInterpolationLimit)
                                .Where(b => double.IsFinite(b.Wh))
                                .Select(b => new EnergyRow(selection.EndpointId, b.BucketStartUtc, b.Wh, EnergySource.IntegratedPower, Estimated: true)));
                        }
                    }

                    await HistorySql.MergeEnergyAsync(connection, transaction, endpoints, rows, start, end, Options.CommandTimeout, cancellationToken)
                        .ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Moves the watermark <paramref name="name"/> towards <paramref name="target"/> one window (at most
    /// <paramref name="batch"/>) per transaction, computing each with <paramref name="compute"/>.
    /// </summary>
    private async Task<(int Batches, DateTimeOffset? Watermark)> RollAsync(
        SqlConnection connection,
        string name,
        TimeSpan bucket,
        TimeSpan batch,
        DateTimeOffset target,
        Func<SqlTransaction, DateTimeOffset, DateTimeOffset, Task> compute,
        CancellationToken cancellationToken)
    {
        var watermark = await HistorySql.GetWatermarkAsync(connection, null, name, Options.CommandTimeout, cancellationToken).ConfigureAwait(false);
        var initialized = false;
        if (watermark is null)
        {
            if (await HistorySql.GetFirstReadingAsync(connection, Options.CommandTimeout, cancellationToken).ConfigureAwait(false) is not { } first)
            {
                return (0, null); // nothing recorded yet
            }

            watermark = RollupMath.Floor(first, bucket);
            initialized = true;
        }

        var step = batch < bucket ? bucket : RollupMath.Floor(DateTimeOffset.UnixEpoch + batch, bucket) - DateTimeOffset.UnixEpoch;
        var batches = 0;
        var start = watermark.Value;
        while (start < target)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = start + step < target ? start + step : target;
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await compute(transaction, start, end).ConfigureAwait(false);
            await HistorySql.SetWatermarkAsync(connection, transaction, name, end, clock.Time.GetUtcNow(), Options.CommandTimeout, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            batches++;
            start = end;
        }

        if (batches == 0 && initialized)
        {
            // Remember where this stage starts, so purges know nothing before it is pending.
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await HistorySql.SetWatermarkAsync(connection, transaction, name, start, clock.Time.GetUtcNow(), Options.CommandTimeout, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return (batches, start);
    }

    private static StatisticRow Combine(int attributeId, DateTimeOffset hour, IReadOnlyList<StatisticRow> parts)
    {
        var measurements = parts.Where(p => p.Mean is not null).ToList();
        if (measurements.Count > 0)
        {
            var combined = RollupMath.CombineMeasurements(
                hour,
                [.. measurements.Select(p => new MeasurementStatistic(p.BucketStartUtc, p.Mean!.Value, p.Min ?? p.Mean!.Value, p.Max ?? p.Mean!.Value))]);
            return new StatisticRow(attributeId, hour, combined.Mean, combined.Min, combined.Max, null, null);
        }

        var counters = RollupMath.CombineCounters(
            hour,
            [.. parts.Where(p => p.Sum is not null).Select(p => new CounterStatistic(p.BucketStartUtc, p.Sum!.Value, p.LastCounter ?? 0))]);
        return new StatisticRow(attributeId, hour, null, null, null, counters.Sum, counters.LastCounter);
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "History rolled up: {ShortTerm} 5-minute, {Hourly} hourly and {Energy} energy windows (watermarks {ShortTermWatermark}, {HourlyWatermark}, {EnergyWatermark})")]
    private partial void LogRolledUp(
        int shortTerm,
        int hourly,
        int energy,
        DateTimeOffset? shortTermWatermark,
        DateTimeOffset? hourlyWatermark,
        DateTimeOffset? energyWatermark);
}

using ControlHome.Application.Abstractions;
using ControlHome.Application.Common;
using ControlHome.Application.Runtime;
using ControlHome.Domain.Devices;
using ControlHome.Domain.History;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Application.History;

/// <summary>
/// Reads history: an attribute's series (raw changes, 5-minute or hourly statistics) and hourly energy. Statistics trail
/// real time: a bucket appears once the rollup closed it (a minute or so after it ended).
/// </summary>
public sealed class HistoryQueryService(IControlHomeDb db, IRuntimeClock clock)
{
    /// <summary>The most points one series returns (the most recent ones).</summary>
    public const int MaxPoints = 5_000;

    public static readonly TimeSpan DefaultRange = TimeSpan.FromHours(24);

    /// <summary>The longest range one request may ask for.</summary>
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(366);

    /// <summary><c>auto</c> serves raw history for ranges up to this long…</summary>
    public static readonly TimeSpan RawUpTo = TimeSpan.FromHours(6);

    /// <summary>…5-minute statistics up to this long, and hourly statistics beyond.</summary>
    public static readonly TimeSpan ShortTermUpTo = TimeSpan.FromDays(7);

    /// <summary>
    /// The most hourly energy rows one request returns (more: 400 <c>energy.too_many_rows</c>). A year of one endpoint
    /// (8 784 hours) always fits; all endpoints need a shorter range.
    /// </summary>
    public const int MaxEnergyRows = 20_000;

    public async Task<Result<HistorySeriesDto>> GetSeriesAsync(int attributeId, HistoryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var resolution = query.Resolution ?? HistoryResolutions.Auto;
        if (!HistoryResolutions.All.Contains(resolution, StringComparer.Ordinal))
        {
            return AppError.Validation(
                ErrorCodes.HistoryInvalidResolution,
                $"resolution must be one of: {string.Join(", ", HistoryResolutions.All)}.");
        }

        if (Range(query.From, query.To) is not { } range)
        {
            return RangeError(query.From, query.To);
        }

        var attribute = await (
                from a in db.DeviceAttributes
                join c in db.EndpointCapabilities on a.EndpointCapabilityId equals c.Id
                where a.Id == attributeId
                select new { a.Key, c.CapabilityId, c.GenericDefinitionJson })
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (attribute is null)
        {
            return AppError.NotFound(ErrorCodes.AttributesNotFound, "No such attribute.");
        }

        var definition = EndpointCapability.ResolveDefinition(attribute.CapabilityId, attribute.GenericDefinitionJson)?.FindAttribute(attribute.Key);
        var hasStatistics = HistoryClassification.KindOf(definition) != StatisticKind.None;
        var unit = definition?.Unit;
        var (from, to) = range;

        if (!hasStatistics)
        {
            return new HistorySeriesDto(attributeId, HistoryResolutions.Raw, unit, (await RawAsync(attributeId, from, to, cancellationToken)).Points);
        }

        var span = to - from;
        switch (resolution)
        {
            case HistoryResolutions.Raw:
                return new HistorySeriesDto(attributeId, HistoryResolutions.Raw, unit, (await RawAsync(attributeId, from, to, cancellationToken)).Points);
            case HistoryResolutions.FiveMinutes:
                return new HistorySeriesDto(attributeId, resolution, unit, (await ShortTermAsync(attributeId, from, to, cancellationToken)).Points);
            case HistoryResolutions.Hourly:
                return new HistorySeriesDto(attributeId, resolution, unit, (await HourlyAsync(attributeId, from, to, cancellationToken)).Points);
        }

        // auto: the finest resolution for the range that fits in MaxPoints and whose rows the range still has — raw
        // history and 5-minute statistics are purged after their retention (hourly statistics are kept forever).
        var retention = await db.RetentionSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken) ?? new RetentionSettings();
        var now = clock.Time.GetUtcNow();
        if (span <= RawUpTo && from >= now - TimeSpan.FromDays(retention.StateLogDays))
        {
            var raw = await RawAsync(attributeId, from, to, cancellationToken);
            if (!raw.Truncated)
            {
                return new HistorySeriesDto(attributeId, HistoryResolutions.Raw, unit, raw.Points);
            }
        }

        if (span <= ShortTermUpTo && from >= now - TimeSpan.FromDays(retention.ShortTermStatsDays))
        {
            var shortTerm = await ShortTermAsync(attributeId, from, to, cancellationToken);
            if (!shortTerm.Truncated)
            {
                return new HistorySeriesDto(attributeId, HistoryResolutions.FiveMinutes, unit, shortTerm.Points);
            }
        }

        return new HistorySeriesDto(attributeId, HistoryResolutions.Hourly, unit, (await HourlyAsync(attributeId, from, to, cancellationToken)).Points);
    }

    public async Task<Result<IReadOnlyList<EnergyHourlyDto>>> GetEnergyHourlyAsync(EnergyQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (Range(query.From, query.To) is not { } range)
        {
            return RangeError(query.From, query.To);
        }

        var rows = db.EnergyHourly.AsNoTracking();
        if (query.EndpointId is { } endpointId)
        {
            if (!await db.Endpoints.AnyAsync(e => e.Id == endpointId, cancellationToken))
            {
                return AppError.NotFound(ErrorCodes.EndpointsNotFound, "No such endpoint.");
            }

            rows = rows.Where(e => e.EndpointId == endpointId);
        }

        var firstHour = RollupMath.Floor(range.From, HourlyStatistic.BucketLength);
        var hours = await rows
            .Where(e => e.HourStartUtc >= firstHour && e.HourStartUtc < range.To)
            .OrderBy(e => e.HourStartUtc)
            .ThenBy(e => e.EndpointId)
            .Take(MaxEnergyRows + 1)
            .ToListAsync(cancellationToken);
        if (hours.Count > MaxEnergyRows)
        {
            // Never a silently cut list: a total over the range would come out too low.
            return AppError.Validation(
                ErrorCodes.EnergyTooManyRows,
                $"The range holds more than {MaxEnergyRows} hourly energy rows: pick a shorter range or one endpoint.");
        }

        return hours.Select(e => new EnergyHourlyDto(e.EndpointId, e.HourStartUtc, e.Wh, EnergySources.Name(e.Source), e.Estimated)).ToList();
    }

    /// <summary>The range asked for (defaults filled in), or <c>null</c> when it is empty, reversed or too long.</summary>
    private (DateTimeOffset From, DateTimeOffset To)? Range(DateTimeOffset? from, DateTimeOffset? to)
    {
        var end = to ?? clock.Time.GetUtcNow();
        var start = from ?? end - DefaultRange;
        return start < end && end - start <= MaxRange ? (start, end) : null;
    }

    private AppError RangeError(DateTimeOffset? from, DateTimeOffset? to)
    {
        var end = to ?? clock.Time.GetUtcNow();
        var start = from ?? end - DefaultRange;
        return start >= end
            ? AppError.Validation(ErrorCodes.HistoryInvalidRange, "from must be before to.")
            : AppError.Validation(ErrorCodes.HistoryRangeTooLarge, $"The range may span at most {MaxRange.TotalDays:0} days.");
    }

    /// <summary>
    /// Changes in <c>[from, to)</c>, preceded by the value in effect at <paramref name="from"/> (as a point at
    /// <paramref name="from"/>) unless the range holds more than <see cref="MaxPoints"/> changes (then the most recent ones).
    /// </summary>
    private async Task<Series> RawAsync(int attributeId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var rows = await db.StateLog.AsNoTracking()
            .Where(s => s.AttributeId == attributeId && s.TsUtc >= from && s.TsUtc < to)
            .OrderByDescending(s => s.TsUtc)
            .Take(MaxPoints + 1)
            .ToListAsync(cancellationToken);
        var truncated = rows.Count > MaxPoints;
        var points = rows.Take(MaxPoints).Reverse().Select(s => new HistoryPointDto(s.TsUtc, s.Num, null, null, s.Str)).ToList();
        if (!truncated && (points.Count == 0 || points[0].T > from))
        {
            var before = await db.StateLog.AsNoTracking()
                .Where(s => s.AttributeId == attributeId && s.TsUtc < from)
                .OrderByDescending(s => s.TsUtc)
                .FirstOrDefaultAsync(cancellationToken);
            if (before is not null)
            {
                points.Insert(0, new HistoryPointDto(from, before.Num, null, null, before.Str));
            }
        }

        return new Series(points, truncated);
    }

    private async Task<Series> ShortTermAsync(int attributeId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        Statistics(await db.ShortTermStatistics.AsNoTracking()
            .Where(s => s.AttributeId == attributeId && s.BucketStartUtc >= RollupMath.Floor(from, ShortTermStatistic.BucketLength) && s.BucketStartUtc < to)
            .OrderByDescending(s => s.BucketStartUtc)
            .Take(MaxPoints + 1)
            .ToListAsync(cancellationToken));

    private async Task<Series> HourlyAsync(int attributeId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        Statistics(await db.HourlyStatistics.AsNoTracking()
            .Where(s => s.AttributeId == attributeId && s.BucketStartUtc >= RollupMath.Floor(from, HourlyStatistic.BucketLength) && s.BucketStartUtc < to)
            .OrderByDescending(s => s.BucketStartUtc)
            .Take(MaxPoints + 1)
            .ToListAsync(cancellationToken));

    /// <summary>Newest-first rows (at most <see cref="MaxPoints"/> + 1) as oldest-first points.</summary>
    private static Series Statistics<T>(List<T> rows)
        where T : AttributeStatistic =>
        new(
            [.. rows.Take(MaxPoints).Reverse().Select(s => s.Mean is { } mean
                ? new HistoryPointDto(s.BucketStartUtc, mean, s.Min, s.Max, null)
                : new HistoryPointDto(s.BucketStartUtc, s.Sum, null, null, null))],
            rows.Count > MaxPoints);

    private sealed record Series(List<HistoryPointDto> Points, bool Truncated);
}

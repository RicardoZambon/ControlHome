namespace ControlHome.Domain.History;

/// <summary>
/// One history reading of a numeric attribute: <see cref="Value"/> (<c>null</c> = unknown) holds from <see cref="TsUtc"/>
/// until the next reading. History stores changes only, so a value lasts until it is replaced.
/// </summary>
public readonly record struct Reading(DateTimeOffset TsUtc, double? Value);

/// <summary>A measurement's statistics over one bucket: time-weighted mean, lowest and highest value.</summary>
public sealed record MeasurementStatistic(DateTimeOffset BucketStartUtc, double Mean, double Min, double Max);

/// <summary>A counter's statistics over one bucket: the increase in it and the counter reading later increases count from.</summary>
public sealed record CounterStatistic(DateTimeOffset BucketStartUtc, double Sum, double LastCounter);

/// <summary>Energy (Wh) integrated from power readings over one bucket.</summary>
public sealed record EnergyBucket(DateTimeOffset BucketStartUtc, double Wh);

/// <summary>How a counter's readings turn into increases.</summary>
public enum CounterKind
{
    /// <summary>Only grows (<c>total_increasing</c>): a drop of more than <see cref="RollupMath.DipTolerance"/> is a reset.</summary>
    TotalIncreasing,

    /// <summary>May go up and down (<c>total</c>, e.g. net energy): every difference counts, with its sign.</summary>
    Total,
}

/// <summary>
/// The arithmetic of the history rollups (pure, on UTC buckets aligned to the Unix epoch). Readings are what
/// <c>StateLog</c> holds: changes only, so each value lasts until the next reading.
/// <list type="bullet">
/// <item><see cref="Measurements"/>: time-weighted mean, min and max per bucket; the value in effect before the range is
/// carried into it; unknown (null) time is left out; a bucket without any known value has no row.</item>
/// <item><see cref="Counters"/>: per bucket the sum of increases since the previous reading (the first reading ever only
/// sets the baseline) and the counter reading later increases are measured from. A <c>total_increasing</c> counter that
/// drops by more than <see cref="DipTolerance"/> was reset: the new reading counts from zero; a smaller dip is noise and
/// keeps the higher reading as the baseline.</item>
/// <item><see cref="IntegratePower"/>: Wh per bucket by the trapezoid rule between readings at most the interpolation
/// limit apart; a longer gap means the value did not change in between (history keeps changes only), so the earlier
/// reading is held until the next one — a plain trapezoid would smear an on/off step over the whole gap.</item>
/// </list>
/// </summary>
public static class RollupMath
{
    /// <summary>A <c>total_increasing</c> counter dropping by less than this fraction is noise, not a reset.</summary>
    public const double DipTolerance = 0.1;

    /// <summary>The start of the bucket <paramref name="time"/> falls in (buckets are aligned to the Unix epoch, in UTC).</summary>
    public static DateTimeOffset Floor(DateTimeOffset time, TimeSpan bucket)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bucket, TimeSpan.Zero);
        var ticks = time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        var floored = ticks - (((ticks % bucket.Ticks) + bucket.Ticks) % bucket.Ticks);
        return new DateTimeOffset(DateTimeOffset.UnixEpoch.UtcTicks + floored, TimeSpan.Zero);
    }

    /// <summary>Time-weighted mean, min and max per bucket of <c>[start, end)</c>.</summary>
    /// <param name="carryIn">The last reading before <paramref name="start"/> (its value holds until the first reading).</param>
    /// <param name="readings">Readings in <c>[start, end)</c>, oldest first (others are ignored).</param>
    public static IReadOnlyList<MeasurementStatistic> Measurements(
        Reading? carryIn,
        IReadOnlyList<Reading> readings,
        DateTimeOffset start,
        DateTimeOffset end,
        TimeSpan bucket)
    {
        ArgumentNullException.ThrowIfNull(readings);
        var count = BucketCount(start, end, bucket);
        var weighted = new double[count];
        var known = new double[count];
        var min = new double[count];
        var max = new double[count];
        Array.Fill(min, double.PositiveInfinity);
        Array.Fill(max, double.NegativeInfinity);

        var points = Points(carryIn is { } carry ? carry with { TsUtc = start } : null, readings, start, end);
        for (var i = 0; i < points.Count; i++)
        {
            if (points[i].Value is not { } value)
            {
                continue;
            }

            var from = points[i].TsUtc;
            var to = i + 1 < points.Count ? points[i + 1].TsUtc : end;
            ForEachPiece(from, to, start, bucket, (index, pieceStart, pieceEnd) =>
            {
                var seconds = (pieceEnd - pieceStart).TotalSeconds;
                weighted[index] += value * seconds;
                known[index] += seconds;
                min[index] = Math.Min(min[index], value);
                max[index] = Math.Max(max[index], value);
            });
        }

        var result = new List<MeasurementStatistic>();
        for (var i = 0; i < count; i++)
        {
            if (known[i] > 0)
            {
                result.Add(new MeasurementStatistic(start + (bucket * i), weighted[i] / known[i], min[i], max[i]));
            }
        }

        return result;
    }

    /// <summary>The increase per bucket of <c>[start, end)</c> and the counter reading at its end.</summary>
    /// <param name="baseline">
    /// The counter reading increases are measured from (the previous bucket's last counter, else the last known reading
    /// before <paramref name="start"/>); <c>null</c> when none is known.
    /// </param>
    /// <param name="readings">Readings in <c>[start, end)</c>, oldest first (others are ignored).</param>
    public static IReadOnlyList<CounterStatistic> Counters(
        double? baseline,
        IReadOnlyList<Reading> readings,
        DateTimeOffset start,
        DateTimeOffset end,
        TimeSpan bucket,
        CounterKind kind)
    {
        ArgumentNullException.ThrowIfNull(readings);
        var count = BucketCount(start, end, bucket);
        var result = new List<CounterStatistic>();
        var next = 0;
        var inRange = readings.Where(r => r.TsUtc >= start && r.TsUtc < end).ToList();
        for (var i = 0; i < count; i++)
        {
            var bucketStart = start + (bucket * i);
            var bucketEnd = bucketStart + bucket;
            var sum = 0d;
            for (; next < inRange.Count && inRange[next].TsUtc < bucketEnd; next++)
            {
                if (inRange[next].Value is not { } value)
                {
                    continue;
                }

                if (baseline is not { } previous)
                {
                    baseline = value;
                    continue;
                }

                if (kind == CounterKind.Total || value >= previous)
                {
                    sum += value - previous;
                    baseline = value;
                }
                else if (value < previous * (1 - DipTolerance))
                {
                    // Reset: the counter started again from zero.
                    sum += Math.Max(value, 0);
                    baseline = value;
                }

                // Otherwise a small dip: noise; the higher reading stays the baseline.
            }

            if (baseline is { } last)
            {
                result.Add(new CounterStatistic(bucketStart, sum, last));
            }
        }

        return result;
    }

    /// <summary>Energy (Wh) per bucket of <c>[start, end)</c> from power readings (W).</summary>
    /// <param name="carryIn">The last reading before <paramref name="start"/>, at its own time (it shapes the start).</param>
    /// <param name="readings">Readings in <c>[start, end)</c>, oldest first (others are ignored).</param>
    /// <param name="next">The first reading at or after <paramref name="end"/>, if already known (it shapes the end).</param>
    /// <param name="interpolationLimit">Readings at most this far apart are joined linearly; farther apart, the earlier one is held.</param>
    public static IReadOnlyList<EnergyBucket> IntegratePower(
        Reading? carryIn,
        IReadOnlyList<Reading> readings,
        Reading? next,
        DateTimeOffset start,
        DateTimeOffset end,
        TimeSpan bucket,
        TimeSpan interpolationLimit)
    {
        ArgumentNullException.ThrowIfNull(readings);
        var count = BucketCount(start, end, bucket);
        var wh = new double[count];
        var known = new bool[count];

        var points = new List<Reading>();
        if (carryIn is { } carry && carry.TsUtc <= start)
        {
            points.Add(carry);
        }

        points.AddRange(readings.Where(r => r.TsUtc >= start && r.TsUtc < end));
        if (next is { } after && after.TsUtc >= end)
        {
            points.Add(after);
        }

        for (var i = 0; i < points.Count; i++)
        {
            var current = points[i];
            if (current.Value is not { } value || current.TsUtc >= end)
            {
                continue;
            }

            var following = i + 1 < points.Count ? points[i + 1] : (Reading?)null;
            var from = current.TsUtc < start ? start : current.TsUtc;
            var to = following is { } f && f.TsUtc < end ? f.TsUtc : end;

            // Joined linearly to the next reading when it is known and close enough; otherwise held (slope 0).
            var slope = 0d;
            if (following is { Value: { } nextValue } joined
                && joined.TsUtc > current.TsUtc
                && joined.TsUtc - current.TsUtc <= interpolationLimit)
            {
                slope = (nextValue - value) / (joined.TsUtc - current.TsUtc).TotalSeconds;
            }

            ForEachPiece(from, to, start, bucket, (index, pieceStart, pieceEnd) =>
            {
                var atStart = value + (slope * (pieceStart - current.TsUtc).TotalSeconds);
                var atEnd = value + (slope * (pieceEnd - current.TsUtc).TotalSeconds);
                wh[index] += (atStart + atEnd) / 2 * (pieceEnd - pieceStart).TotalHours;
                known[index] = true;
            });
        }

        var result = new List<EnergyBucket>();
        for (var i = 0; i < count; i++)
        {
            if (known[i])
            {
                result.Add(new EnergyBucket(start + (bucket * i), wh[i]));
            }
        }

        return result;
    }

    /// <summary>A longer bucket from shorter ones: the mean of their means, the lowest min and the highest max.</summary>
    public static MeasurementStatistic CombineMeasurements(DateTimeOffset bucketStartUtc, IReadOnlyList<MeasurementStatistic> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0)
        {
            throw new ArgumentException("At least one bucket is needed.", nameof(parts));
        }

        return new MeasurementStatistic(bucketStartUtc, parts.Average(p => p.Mean), parts.Min(p => p.Min), parts.Max(p => p.Max));
    }

    /// <summary>A longer bucket from shorter ones: the sum of their increases and the latest one's last counter.</summary>
    public static CounterStatistic CombineCounters(DateTimeOffset bucketStartUtc, IReadOnlyList<CounterStatistic> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0)
        {
            throw new ArgumentException("At least one bucket is needed.", nameof(parts));
        }

        var latest = parts.MaxBy(p => p.BucketStartUtc)!;
        return new CounterStatistic(bucketStartUtc, parts.Sum(p => p.Sum), latest.LastCounter);
    }

    private static int BucketCount(DateTimeOffset start, DateTimeOffset end, TimeSpan bucket)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bucket, TimeSpan.Zero);
        if (Floor(start, bucket) != start || Floor(end, bucket) != end || end < start)
        {
            throw new ArgumentException($"The range {start:O} – {end:O} is not made of whole {bucket} buckets.", nameof(start));
        }

        return checked((int)((end - start).Ticks / bucket.Ticks));
    }

    /// <summary>The carried reading (moved to <paramref name="start"/>) followed by the readings in <c>[start, end)</c>.</summary>
    private static List<Reading> Points(Reading? carryIn, IReadOnlyList<Reading> readings, DateTimeOffset start, DateTimeOffset end)
    {
        var points = new List<Reading>(readings.Count + 1);
        if (carryIn is { } carry)
        {
            points.Add(carry);
        }

        points.AddRange(readings.Where(r => r.TsUtc >= start && r.TsUtc < end));
        return points;
    }

    /// <summary>Splits <c>[from, to)</c> at bucket boundaries (buckets counted from <paramref name="start"/>).</summary>
    private static void ForEachPiece(
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset start,
        TimeSpan bucket,
        Action<int, DateTimeOffset, DateTimeOffset> piece)
    {
        var at = from;
        while (at < to)
        {
            var index = (int)((at - start).Ticks / bucket.Ticks);
            var bucketEnd = start + (bucket * (index + 1));
            var pieceEnd = bucketEnd < to ? bucketEnd : to;
            piece(index, at, pieceEnd);
            at = pieceEnd;
        }
    }
}

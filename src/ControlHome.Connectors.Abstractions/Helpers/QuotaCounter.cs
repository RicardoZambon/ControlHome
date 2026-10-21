using System.Globalization;

namespace ControlHome.Connectors.Abstractions;

/// <summary>The calendar period (UTC) a quota counter resets on.</summary>
public enum QuotaPeriod
{
    Minute,
    Hour,
    Day,
    Month,
}

/// <summary>A vendor limit, e.g. 10 000 API calls a month.</summary>
/// <param name="Counter">
/// Global counter name: prefix it with the connector key and integration id (e.g. <c>tuya:{integrationId}:calls</c>).
/// </param>
public sealed record QuotaLimit(string Counter, QuotaPeriod Period, long Limit);

/// <summary>
/// Usage counters per calendar period, so connectors respect vendor quotas across restarts. The host provides a
/// database-backed implementation through DI (inject it into the connector factory); <see cref="InMemoryQuotaCounter"/>
/// serves tests.
/// </summary>
public interface IQuotaCounter
{
    /// <summary>Adds <paramref name="amount"/> to the counter's current period and returns the period's new total.</summary>
    ValueTask<long> AddAsync(string counter, QuotaPeriod period, long amount, CancellationToken cancellationToken);

    /// <summary>The counter's total in the current period (0 when unused).</summary>
    ValueTask<long> GetAsync(string counter, QuotaPeriod period, CancellationToken cancellationToken);
}

public static class QuotaCounterExtensions
{
    /// <summary>
    /// Counts <paramref name="amount"/> against <paramref name="limit"/> if it fits; returns <c>false</c> (and counts
    /// nothing) when it would exceed the limit — then set <c>IntegrationStatus.QuotaExceeded</c> and wait for the next
    /// period. Not atomic across processes; only the single active runtime calls it.
    /// </summary>
    public static async ValueTask<bool> TryConsumeAsync(
        this IQuotaCounter counter,
        QuotaLimit limit,
        long amount = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(counter);
        ArgumentNullException.ThrowIfNull(limit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);

        var used = await counter.GetAsync(limit.Counter, limit.Period, cancellationToken).ConfigureAwait(false);
        if (used + amount > limit.Limit)
        {
            return false;
        }

        await counter.AddAsync(limit.Counter, limit.Period, amount, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>The start (UTC) of the period containing <paramref name="nowUtc"/>.</summary>
    public static DateTimeOffset PeriodStart(QuotaPeriod period, DateTimeOffset nowUtc)
    {
        var utc = nowUtc.ToUniversalTime();
        return period switch
        {
            QuotaPeriod.Minute => new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero),
            QuotaPeriod.Hour => new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero),
            QuotaPeriod.Day => new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero),
            QuotaPeriod.Month => new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero),
            _ => throw new ArgumentOutOfRangeException(nameof(period), period, null),
        };
    }
}

/// <summary>An in-process <see cref="IQuotaCounter"/> (lost on restart): tests and connectors without persisted quotas.</summary>
public sealed class InMemoryQuotaCounter(TimeProvider time) : IQuotaCounter
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, long> _totals = new(StringComparer.Ordinal);

    public ValueTask<long> AddAsync(string counter, QuotaPeriod period, long amount, CancellationToken cancellationToken)
    {
        var key = Key(counter, period);
        lock (_lock)
        {
            var total = _totals.GetValueOrDefault(key) + amount;
            _totals[key] = total;
            return ValueTask.FromResult(total);
        }
    }

    public ValueTask<long> GetAsync(string counter, QuotaPeriod period, CancellationToken cancellationToken)
    {
        var key = Key(counter, period);
        lock (_lock)
        {
            return ValueTask.FromResult(_totals.GetValueOrDefault(key));
        }
    }

    private string Key(string counter, QuotaPeriod period) =>
        string.Create(CultureInfo.InvariantCulture, $"{counter}|{period}|{QuotaCounterExtensions.PeriodStart(period, time.GetUtcNow()):O}");
}

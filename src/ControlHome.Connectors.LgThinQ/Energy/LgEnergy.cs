using System.Globalization;
using System.Text.Json;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.LgThinQ.Api;

namespace ControlHome.Connectors.LgThinQ.Energy;

/// <summary>The date ranges one nightly energy run asks LG for (dates in the account's zone).</summary>
/// <param name="Today">The local day of the run (its data is not complete yet, so never asked for).</param>
internal sealed record LgEnergyQuery(DateOnly Today, DateOnly DailyFrom, DateOnly DailyTo, DateOnly MonthlyFrom, DateOnly MonthlyTo);

/// <summary>
/// LG energy is history, not live power: Wh per day (<c>DAILY</c>) or month (<c>MONTHLY</c>), complete up to yesterday. Once a
/// night, at a random time between 01:00 and 05:59 local (as LG's own integration does, to spread the load), ControlHome
/// asks for every day since the first of last month and for last and this month, and reports them as energy windows in one
/// report: the days as they are, and each month only for the days its daily data leaves out (the host keeps finer windows
/// and spreads a month's remainder). Two calls per energy property and night.
/// </summary>
internal static class LgEnergy
{
    public const string Daily = "DAILY";
    public const string Monthly = "MONTHLY";

    /// <summary>The earliest local start of the nightly run.</summary>
    public static readonly TimeSpan WindowStart = TimeSpan.FromHours(1);

    /// <summary>How long after <see cref="WindowStart"/> the run may start (01:00–05:59).</summary>
    public const int WindowMinutes = 300;

    /// <summary>Field names an entry's date is looked for under first (LG's other APIs use <c>usedDate</c>).</summary>
    private static readonly string[] _dateFields = ["usedDate", "date", "usageDate", "useDate", "usedDay", "day", "usedMonth", "month", "period"];

    /// <summary>A random local time of day in 01:00–05:59 (chosen once per integration, so restarts keep it).</summary>
    public static TimeSpan RandomTimeOfDay(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        return WindowStart + TimeSpan.FromMinutes(random.Next(0, WindowMinutes)) + TimeSpan.FromSeconds(random.Next(0, 60));
    }

    /// <summary>
    /// The next instant (UTC, after <paramref name="nowUtc"/>) at local wall-clock <paramref name="timeOfDay"/> in
    /// <paramref name="zone"/> (on a day with a daylight-saving change too; a time the change skips moves to the first
    /// valid one after it).
    /// </summary>
    public static DateTimeOffset NextRun(DateTimeOffset nowUtc, TimeZoneInfo zone, TimeSpan timeOfDay)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime);
        for (var day = localToday; ; day = day.AddDays(1))
        {
            var run = ToUtc(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified) + timeOfDay, zone);
            if (run > nowUtc)
            {
                return run;
            }
        }
    }

    /// <summary>What a run at <paramref name="nowUtc"/> asks for: days from the first of last month to yesterday, and last and this month.</summary>
    public static LgEnergyQuery Query(DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime);
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var lastMonth = thisMonth.AddMonths(-1);
        return new LgEnergyQuery(today, lastMonth, today.AddDays(-1), lastMonth, thisMonth);
    }

    /// <summary>
    /// The energy windows of one property from LG's DAILY and MONTHLY answers (<c>{result: {dataList: [{&lt;date&gt;, &lt;property&gt;}]}}</c>):
    /// days as local-midnight-to-midnight windows, months from their first day to the end of the month or the start of
    /// today, whichever comes first (never into the future). Entries of the same day (or month) add up — LG's SDK sums
    /// <c>dataList</c>, and a day may come as several rows. Entries without a usable date or value are skipped.
    /// </summary>
    public static IReadOnlyList<EnergyWindowReport> Windows(
        string deviceId,
        string endpoint,
        string property,
        JsonElement? daily,
        JsonElement? monthly,
        LgEnergyQuery query,
        TimeZoneInfo zone)
    {
        var windows = new List<EnergyWindowReport>();
        var todayStart = ToUtc(query.Today, zone);
        foreach (var (date, wh) in Entries(daily, property, Daily, query.DailyFrom, query.DailyTo))
        {
            var start = ToUtc(date, zone);
            var end = ToUtc(date.AddDays(1), zone);
            if (end <= todayStart && end > start)
            {
                windows.Add(new EnergyWindowReport(deviceId, endpoint, start, end, wh));
            }
        }

        foreach (var (date, wh) in Entries(monthly, property, Monthly, query.MonthlyFrom, query.MonthlyTo))
        {
            var month = new DateOnly(date.Year, date.Month, 1);
            var start = ToUtc(month, zone);
            var monthEnd = ToUtc(month.AddMonths(1), zone);
            var end = monthEnd < todayStart ? monthEnd : todayStart;
            if (end > start)
            {
                windows.Add(new EnergyWindowReport(deviceId, endpoint, start, end, wh));
            }
        }

        return windows;
    }

    /// <summary>A local midnight in UTC (a midnight skipped by a DST change becomes the first valid hour of that day).</summary>
    public static DateTimeOffset ToUtc(DateOnly day, TimeZoneInfo zone) => ToUtc(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone);

    /// <summary>A local wall-clock time in UTC; a time skipped by a DST change moves to the first valid one (30-minute steps).</summary>
    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone)
    {
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    /// <summary>One day's window (a local midnight to the next), e.g. from a one-day query's <see cref="Total"/>.</summary>
    public static EnergyWindowReport Day(string deviceId, string endpoint, DateOnly day, double wh, TimeZoneInfo zone) =>
        new(deviceId, endpoint, ToUtc(day, zone), ToUtc(day.AddDays(1), zone), wh);

    /// <summary>
    /// Whether an answer has energy entries but not one whose date could be read (LG does not document the field): its
    /// entries cannot be placed on days, only summed (<see cref="Total"/>) for a query of a single period.
    /// </summary>
    public static bool IsUndated(JsonElement? answer, string property, string period, DateOnly from, DateOnly to)
    {
        var rows = Rows(answer, property, period).ToList();
        return rows.Count > 0 && rows.All(r => r.Date is null)
            && !(rows.Count == 1 && SinglePeriod(period, from, to));
    }

    /// <summary>
    /// The sum of <paramref name="property"/> over the answer's <c>dataList</c> (what LG's SDK returns for a query), or
    /// <c>null</c> when the answer has no list.
    /// </summary>
    public static double? Total(JsonElement? answer, string property)
    {
        if (answer is not { } response || (response.Get("result") ?? response).Get("dataList") is not { ValueKind: JsonValueKind.Array } list)
        {
            return null;
        }

        return list.EnumerateArray().Sum(entry => entry.Num(property) is { } wh && wh >= 0 ? wh : 0);
    }

    /// <summary>The entries of the range, summed per day (DAILY) or month (MONTHLY), oldest first.</summary>
    private static IEnumerable<(DateOnly Date, double Wh)> Entries(JsonElement? answer, string property, string period, DateOnly from, DateOnly to)
    {
        var rows = Rows(answer, property, period).ToList();
        var first = period == Daily ? from : new DateOnly(from.Year, from.Month, 1);
        var sums = new SortedDictionary<DateOnly, double>();
        foreach (var (date, wh) in rows)
        {
            // One period asked for, one undated answer: that period.
            var day = date ?? (rows.Count == 1 && SinglePeriod(period, from, to) ? first : null);
            if (day is { } known && known >= first && known <= to)
            {
                sums[known] = sums.GetValueOrDefault(known) + wh;
            }
        }

        return sums.Select(s => (s.Key, s.Value));
    }

    /// <summary>The answer's entries with a value: their date (when one could be read) and their Wh.</summary>
    private static IEnumerable<(DateOnly? Date, double Wh)> Rows(JsonElement? answer, string property, string period)
    {
        if (answer is not { } response)
        {
            yield break;
        }

        foreach (var entry in (response.Get("result") ?? response).Array("dataList"))
        {
            var value = entry.Num(property) ?? entry.Num("energyUsage") ?? entry.Num("value");
            if (value is { } wh && wh >= 0)
            {
                yield return (DateOf(entry, property, period), wh);
            }
        }
    }

    private static bool SinglePeriod(string period, DateOnly from, DateOnly to) =>
        period == Daily ? from == to : (from.Year, from.Month) == (to.Year, to.Month);

    /// <summary>
    /// An entry's date: from a field named like a date (<c>usedDate</c>, <c>date</c>, …), else from any other member holding
    /// a date of the period's shape (<c>20261020</c>, <c>"2026-10-20"</c>, <c>"202610"</c>). Values, epoch timestamps and
    /// anything else are not dates.
    /// </summary>
    private static DateOnly? DateOf(JsonElement entry, string property, string period)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in _dateFields)
        {
            if (entry.Get(name) is { } named && ParseDate(named, period) is { } date)
            {
                return date;
            }
        }

        foreach (var member in entry.EnumerateObject())
        {
            if (member.Name != property && !_dateFields.Contains(member.Name, StringComparer.Ordinal) && ParseDate(member.Value, period) is { } date)
            {
                return date;
            }
        }

        return null;
    }

    private static DateOnly? ParseDate(JsonElement value, string period)
    {
        string digits;
        switch (value.ValueKind)
        {
            case JsonValueKind.String when value.GetString() is { Length: <= 40 } text:
                digits = new string([.. text.Where(char.IsAsciiDigit)]);
                break;
            case JsonValueKind.Number when value.TryGetInt64(out var number) && number > 0:
                // A bare number is a date only with exactly the period's digits (20261020, 202610): never a value or a timestamp.
                digits = number.ToString(CultureInfo.InvariantCulture);
                if (digits.Length != (period == Daily ? 8 : 6))
                {
                    return null;
                }

                break;
            default:
                return null;
        }

        return period == Daily
            ? digits.Length >= 8 && DateOnly.TryParseExact(digits[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null
            : digits.Length is 6 or >= 8 && DateOnly.TryParseExact(digits[..6] + "01", "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month) ? month : null;
    }
}

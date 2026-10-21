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

    /// <summary>A random local time of day in 01:00–05:59 (chosen once per integration, so restarts keep it).</summary>
    public static TimeSpan RandomTimeOfDay(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        return WindowStart + TimeSpan.FromMinutes(random.Next(0, WindowMinutes)) + TimeSpan.FromSeconds(random.Next(0, 60));
    }

    /// <summary>The next instant (UTC, after <paramref name="nowUtc"/>) at local <paramref name="timeOfDay"/> in <paramref name="zone"/>.</summary>
    public static DateTimeOffset NextRun(DateTimeOffset nowUtc, TimeZoneInfo zone, TimeSpan timeOfDay)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime);
        for (var day = localToday; ; day = day.AddDays(1))
        {
            var run = ToUtc(day, zone) + timeOfDay;
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
    /// The energy windows of one property from LG's DAILY and MONTHLY answers (<c>{result: {dataList: [{usedDate, &lt;property&gt;}]}}</c>):
    /// days as local-midnight-to-midnight windows, months from their first day to the end of the month or the start of
    /// today, whichever comes first (never into the future). Entries without a usable date or value are skipped.
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
    public static DateTimeOffset ToUtc(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    private static IEnumerable<(DateOnly Date, double Wh)> Entries(JsonElement? answer, string property, string period, DateOnly from, DateOnly to)
    {
        if (answer is not { } response)
        {
            yield break;
        }

        var list = (response.Get("result") ?? response).Array("dataList").ToList();
        foreach (var entry in list)
        {
            var value = entry.Num(property) ?? entry.Num("energyUsage") ?? entry.Num("value");
            if (value is not { } wh || wh < 0)
            {
                continue;
            }

            var date = ParseDate(entry.Str("usedDate") ?? entry.Str("date") ?? entry.Str("usageDate"), period);
            if (date is null && list.Count == 1 && (period == Daily ? from == to : (from.Year, from.Month) == (to.Year, to.Month)))
            {
                date = from; // one period asked for, one answer
            }

            if (date is { } day && day >= (period == Daily ? from : new DateOnly(from.Year, from.Month, 1)) && day <= to)
            {
                yield return (day, wh);
            }
        }
    }

    private static DateOnly? ParseDate(string? text, string period)
    {
        if (text is null)
        {
            return null;
        }

        var digits = new string([.. text.Where(char.IsAsciiDigit)]);
        return period == Daily
            ? digits.Length >= 8 && DateOnly.TryParseExact(digits[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null
            : digits.Length >= 6 && DateOnly.TryParseExact(digits[..6] + "01", "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month) ? month : null;
    }
}

namespace ControlHome.Domain.History;

/// <summary>Energy an endpoint used in <c>[StartUtc, EndUtc)</c>, as a vendor reports it (a day, a month, 15 minutes).</summary>
public sealed record EnergyWindow(DateTimeOffset StartUtc, DateTimeOffset EndUtc, double Wh)
{
    public TimeSpan Duration => EndUtc - StartUtc;
}

/// <summary>One <c>EnergyHourly</c> row computed from energy windows.</summary>
/// <param name="Estimated">Spread from a window longer than an hour ("not hourly"), not measured per hour.</param>
public readonly record struct EnergyHour(DateTimeOffset HourStartUtc, double Wh, bool Estimated);

/// <summary>
/// Turns vendor energy windows into hourly energy (pure; UTC hours). The rows of one report:
/// <list type="bullet">
/// <item>Windows of up to an hour are measured data: each adds its energy to the hour(s) it overlaps, in proportion to the
/// overlap (15-minute deltas add up within their hour).</item>
/// <item>Longer windows are snapped to whole UTC hours (start and end rounded to the nearest hour, so a day starting at a
/// half-hour local midnight still sits next to the following day) and, shortest first, spread evenly over the hours no
/// finer window of the report covers: a window's energy minus what those finer windows put inside it (never below zero).
/// A month reported with its days therefore only fills the days without daily data, and a window fully covered by finer
/// ones adds nothing. Spread hours are <see cref="EnergyHour.Estimated"/>.</item>
/// </list>
/// The result depends only on the set of windows (never on their order), so reporting the same windows again gives the
/// same rows.
/// </summary>
public static class EnergyWindowMath
{
    public static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    /// <summary>The longest window accepted (a leap year): bounds the rows one window can produce.</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(366);

    /// <summary>Why <paramref name="window"/> cannot be used, or <c>null</c> when it can.</summary>
    public static string? Validate(EnergyWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!double.IsFinite(window.Wh) || window.Wh < 0)
        {
            return "the energy must be a finite, non-negative number of Wh";
        }

        if (window.EndUtc <= window.StartUtc)
        {
            return "the window must end after it starts";
        }

        return window.Duration > MaxWindow ? "the window is longer than a year" : null;
    }

    /// <summary>The hourly rows of one endpoint's report, oldest first.</summary>
    /// <exception cref="ArgumentException">A window fails <see cref="Validate"/>.</exception>
    public static IReadOnlyList<EnergyHour> Spread(IReadOnlyList<EnergyWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        foreach (var window in windows)
        {
            if (Validate(window) is { } problem)
            {
                throw new ArgumentException($"Invalid energy window {window.StartUtc:O}–{window.EndUtc:O}: {problem}.", nameof(windows));
            }
        }

        var hours = new SortedDictionary<DateTimeOffset, (double Wh, bool Estimated)>();

        // Measured windows, in a fixed order so the sums do not depend on the report's order.
        // A window repeated in one report counts once.
        foreach (var window in windows.Where(w => w.Duration <= Hour).Distinct().OrderBy(w => w.StartUtc).ThenBy(w => w.EndUtc).ThenBy(w => w.Wh))
        {
            for (var hour = RollupMath.Floor(window.StartUtc, Hour); hour < window.EndUtc; hour += Hour)
            {
                var overlap = Min(window.EndUtc, hour + Hour) - Max(window.StartUtc, hour);
                if (overlap > TimeSpan.Zero)
                {
                    var share = window.Wh * (overlap.Ticks / (double)window.Duration.Ticks);
                    hours[hour] = (hours.GetValueOrDefault(hour).Wh + share, false);
                }
            }
        }

        var spread = windows
            .Where(w => w.Duration > Hour)
            .Select(w => (Start: Round(w.StartUtc), End: Round(w.EndUtc), w.Wh))
            .Distinct()
            .OrderBy(w => w.End - w.Start)
            .ThenBy(w => w.Start)
            .ThenBy(w => w.Wh);
        foreach (var (start, end, wh) in spread)
        {
            var covered = 0.0;
            var free = new List<DateTimeOffset>();
            for (var hour = start; hour < end; hour += Hour)
            {
                if (hours.TryGetValue(hour, out var existing))
                {
                    covered += existing.Wh;
                }
                else
                {
                    free.Add(hour);
                }
            }

            if (free.Count == 0)
            {
                continue;
            }

            var share = Math.Max(0, wh - covered) / free.Count;
            foreach (var hour in free)
            {
                hours[hour] = (share, true);
            }
        }

        return [.. hours.Select(h => new EnergyHour(h.Key, h.Value.Wh, h.Value.Estimated))];
    }

    /// <summary>The nearest UTC hour (half an hour rounds up).</summary>
    private static DateTimeOffset Round(DateTimeOffset time) => RollupMath.Floor(time + TimeSpan.FromMinutes(30), Hour);

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}

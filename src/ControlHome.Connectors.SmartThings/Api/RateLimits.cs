using System.Globalization;
using System.Net.Http.Headers;

namespace ControlHome.Connectors.SmartThings.Api;

/// <summary>The <c>X-RateLimit-*</c> headers of one response.</summary>
/// <param name="ResetRaw">The raw <c>X-RateLimit-Reset</c> number (its unit is ambiguous: see <see cref="RateLimitHeaders.ResetAfter"/>).</param>
internal sealed record RateLimitHeaders(long? Limit, long? Remaining, double? ResetRaw)
{
    /// <summary>The longest wait a reset header can impose (the documented windows are 60 s and 900 s).</summary>
    public static readonly TimeSpan MaxReset = TimeSpan.FromMinutes(15);

    public bool IsEmpty => Limit is null && Remaining is null && ResetRaw is null;

    public static RateLimitHeaders Parse(HttpResponseHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        return new RateLimitHeaders(Integer(headers, "X-RateLimit-Limit"), Integer(headers, "X-RateLimit-Remaining"), Number(headers, "X-RateLimit-Reset"));
    }

    /// <summary>
    /// How long until the window resets. SmartThings' documents disagree on the unit of <c>X-RateLimit-Reset</c> (the
    /// Swagger spec and API overview say milliseconds, the rate-limit and troubleshooting pages say seconds), so: values
    /// that look like epoch timestamps (≥ 10⁹ s or ≥ 10¹² ms) are absolute; values above 1000 are milliseconds; smaller
    /// ones are seconds — unless this bucket has already shown milliseconds (<paramref name="milliseconds"/>). Clamped to
    /// 0 … <see cref="MaxReset"/>.
    /// </summary>
    public static TimeSpan? ResetAfter(double? raw, DateTimeOffset nowUtc, bool milliseconds)
    {
        if (raw is not { } value || !double.IsFinite(value) || value < 0)
        {
            return null;
        }

        TimeSpan wait;
        if (value >= 1e12)
        {
            wait = DateTimeOffset.FromUnixTimeMilliseconds((long)value) - nowUtc;
        }
        else if (value >= 1e9)
        {
            wait = DateTimeOffset.FromUnixTimeSeconds((long)value) - nowUtc;
        }
        else if (value > 1000 || milliseconds)
        {
            wait = TimeSpan.FromMilliseconds(value);
        }
        else
        {
            wait = TimeSpan.FromSeconds(value);
        }

        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait > MaxReset ? MaxReset : wait;
    }

    /// <summary>Whether a raw reset value shows that the server counts in milliseconds.</summary>
    public static bool ShowsMilliseconds(double? raw) => raw is > 1000 and < 1e9;

    private static long? Integer(HttpResponseHeaders headers, string name) =>
        headers.TryGetValues(name, out var values)
        && long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        && value >= 0
            ? value
            : null;

    private static double? Number(HttpResponseHeaders headers, string name) =>
        headers.TryGetValues(name, out var values)
        && double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}

/// <summary>
/// One rate-limit bucket (the device list, a device's status/commands, rooms, …) as the last responses described it,
/// plus any block imposed by a 429. Thread-safe.
/// </summary>
internal sealed class RateBudget
{
    private readonly Lock _lock = new();
    private long? _limit;
    private long? _remaining;
    private DateTimeOffset? _resetAt;
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;
    private bool _milliseconds;

    public long? Limit
    {
        get
        {
            lock (_lock)
            {
                return _limit;
            }
        }
    }

    public long? Remaining
    {
        get
        {
            lock (_lock)
            {
                return _remaining;
            }
        }
    }

    /// <summary>Whether the reset header of this bucket has been seen in milliseconds.</summary>
    public bool UsesMilliseconds
    {
        get
        {
            lock (_lock)
            {
                return _milliseconds;
            }
        }
    }

    public void Observe(RateLimitHeaders headers, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (headers.IsEmpty)
        {
            return;
        }

        lock (_lock)
        {
            _milliseconds |= RateLimitHeaders.ShowsMilliseconds(headers.ResetRaw);
            _limit = headers.Limit ?? _limit;
            _remaining = headers.Remaining;
            _resetAt = RateLimitHeaders.ResetAfter(headers.ResetRaw, nowUtc, _milliseconds) is { } after ? nowUtc + after : null;
        }
    }

    /// <summary>No call before <paramref name="untilUtc"/> (a 429).</summary>
    public void Block(DateTimeOffset untilUtc)
    {
        lock (_lock)
        {
            if (untilUtc > _blockedUntil)
            {
                _blockedUntil = untilUtc;
            }
        }
    }

    /// <summary>The reset of the current window (when the headers named one) relative to <paramref name="nowUtc"/>.</summary>
    public TimeSpan? ResetAfter(DateTimeOffset nowUtc)
    {
        lock (_lock)
        {
            return _resetAt is { } at && at > nowUtc ? at - nowUtc : null;
        }
    }

    /// <summary>
    /// When <paramref name="calls"/> more calls fit while keeping <paramref name="reserve"/> spare: now, or the end of a
    /// 429 block or of the current window when it is nearly used up.
    /// </summary>
    public DateTimeOffset AvailableAt(DateTimeOffset nowUtc, int calls = 1, int reserve = 0)
    {
        lock (_lock)
        {
            var at = _blockedUntil > nowUtc ? _blockedUntil : nowUtc;
            if (_remaining is { } remaining && remaining - calls < reserve && _resetAt is { } reset && reset > at)
            {
                at = reset;
            }

            return at;
        }
    }

    /// <summary>The share of the window still available (1 when unknown).</summary>
    public double Headroom()
    {
        lock (_lock)
        {
            return _limit is { } limit and > 0 && _remaining is { } remaining ? Math.Clamp((double)remaining / limit, 0, 1) : 1;
        }
    }
}

/// <summary>The rate-limit buckets of one integration, by name.</summary>
internal sealed class RateLimitTracker
{
    public const string Devices = "devices";
    public const string Locations = "locations";
    public const string Rooms = "rooms";
    public const string Capabilities = "capabilities";
    public const string OAuth = "oauth";

    private readonly Lock _lock = new();
    private readonly Dictionary<string, RateBudget> _budgets = new(StringComparer.Ordinal);

    /// <summary>The bucket of one device's status and commands.</summary>
    public static string Device(string deviceId) => "device:" + deviceId;

    public RateBudget this[string bucket]
    {
        get
        {
            lock (_lock)
            {
                if (!_budgets.TryGetValue(bucket, out var budget))
                {
                    budget = new RateBudget();
                    _budgets[bucket] = budget;
                }

                return budget;
            }
        }
    }
}

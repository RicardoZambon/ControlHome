using ControlHome.Connectors.SmartThings.Auth;

namespace ControlHome.Connectors.SmartThings;

/// <summary>Where the connector talks to and how it paces itself (defaults for production; tests point it at fakes).</summary>
internal sealed record SmartThingsOptions
{
    public static SmartThingsOptions Default { get; } = new();

    /// <summary>The REST API (every path is relative to it).</summary>
    public Uri ApiBaseUrl { get; init; } = new("https://api.smartthings.com/v1/");

    /// <summary>The documented OAuth endpoints (<c>developer.smartthings.com/docs/service-integrations/oauth</c>).</summary>
    public OAuthEndpoints DocumentedEndpoints { get; init; } = new(
        new Uri("https://api.smartthings.com/v1/oauth/authorize"),
        new Uri("https://api.smartthings.com/v1/oauth/token"));

    /// <summary>The endpoints Home Assistant's SmartThings integration uses.</summary>
    public OAuthEndpoints HomeAssistantEndpoints { get; init; } = new(
        new Uri("https://api.smartthings.com/oauth/authorize"),
        new Uri("https://auth-global.api.smartthings.com/oauth/token"));

    /// <summary>
    /// Which custom OAuth URLs are accepted: HTTPS on <c>smartthings.com</c> or one of its subdomains (the client secret
    /// and the authorization code go there, so no other host).
    /// </summary>
    public Func<Uri, bool> IsAllowedOAuthUrl { get; init; } = DefaultAllowedOAuthUrl;

    /// <summary>One HTTP attempt (connect + answer), on the connector's clock.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Extra attempts for idempotent GETs after a network error, timeout or 5xx (never for POSTs).</summary>
    public int GetRetries { get; init; } = 2;

    public TimeSpan GetRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Refresh when less than this is left of the access token (24 h tokens: about once a day).</summary>
    public TimeSpan RefreshBeforeExpiry { get; init; } = TimeSpan.FromHours(3);

    /// <summary>Refresh at least this often, so the refresh token (about 30 days without use) never lapses.</summary>
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromDays(7);

    /// <summary>The bulk poll interval when the configuration gives none.</summary>
    public TimeSpan DefaultPollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The longest the bulk poll is stretched to while the rate-limit headers say the budget runs low.</summary>
    public TimeSpan MaxPollInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>After a command, the device is polled this often …</summary>
    public TimeSpan HotPollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>… for this long.</summary>
    public TimeSpan HotPollWindow { get; init; } = TimeSpan.FromSeconds(30);

    public double Jitter { get; init; } = 0.1;

    /// <summary>Randomness for the jitter (seeded in tests).</summary>
    public Random? Random { get; init; }

    /// <summary>Status reads and commands per device and minute (SmartThings: 12).</summary>
    public int DeviceRequestsPerMinute { get; init; } = 12;

    /// <summary>Requests per device and minute kept free for commands (hot polls leave them alone).</summary>
    public int CommandReserve { get; init; } = 2;

    /// <summary>Device list pages per minute (SmartThings: 12 for "other device APIs"; kept below).</summary>
    public int ListRequestsPerMinute { get; init; } = 10;

    /// <summary>Most pages followed per device list (300 devices per location at most).</summary>
    public int MaxPages { get; init; } = 20;

    /// <summary>Rooms are cached this long (the rooms API allows 50 calls an hour).</summary>
    public TimeSpan RoomsRefresh { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Rooms calls allowed per hour (persisted quota, below SmartThings' 50).</summary>
    public int RoomsPerHour { get; init; } = 40;

    /// <summary>Capability definitions fetched per poll at most.</summary>
    public int DefinitionFetchesPerPoll { get; init; } = 10;

    /// <summary>How long a capability definition is cached.</summary>
    public TimeSpan DefinitionLifetime { get; init; } = TimeSpan.FromDays(3);

    /// <summary>How long before a definition that could not be fetched is tried again.</summary>
    public TimeSpan DefinitionRetry { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Idempotency keys are remembered this long (a repeated key is a duplicate).</summary>
    public TimeSpan IdempotencyWindow { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan TransientBackoffInitial { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan TransientBackoffMax { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>While SmartThings asks for a paid plan, try again this often.</summary>
    public TimeSpan PlanRequiredRetry { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>While the tokens are rejected, check again this often (the reauth flow restarts the runtime anyway).</summary>
    public TimeSpan NeedsReauthRetry { get; init; } = TimeSpan.FromHours(1);

    /// <summary>After an unexpected (permanent) error, try again after this.</summary>
    public TimeSpan ErrorRetry { get; init; } = TimeSpan.FromMinutes(5);

    public static bool DefaultAllowedOAuthUrl(Uri url) =>
        url is { IsAbsoluteUri: true }
        && url.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(url.UserInfo)
        && (url.Host.Equals("smartthings.com", StringComparison.OrdinalIgnoreCase)
            || url.Host.EndsWith(".smartthings.com", StringComparison.OrdinalIgnoreCase));
}

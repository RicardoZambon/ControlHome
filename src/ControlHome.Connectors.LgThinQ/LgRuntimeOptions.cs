using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using ControlHome.Connectors.LgThinQ.Api;
using ControlHome.Connectors.LgThinQ.Model;

namespace ControlHome.Connectors.LgThinQ;

/// <summary>Timing and limits of the LG ThinQ runtime (tests shorten them).</summary>
internal sealed record LgRuntimeOptions
{
    public static LgRuntimeOptions Default { get; } = new();

    /// <summary>First delay after a lost or failed MQTT connection (doubling, with jitter).</summary>
    public TimeSpan ReconnectInitial { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Longest delay between MQTT connection attempts.</summary>
    public TimeSpan ReconnectMax { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Relative jitter of reconnect and retry delays (0–0.5).</summary>
    public double Jitter { get; init; } = 0.2;

    /// <summary>A device's state is polled at most this often (startup, after a reconnect, while MQTT is down).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How often the "MQTT is down" poller checks which devices are due.</summary>
    public TimeSpan PollCheckInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Push and event subscriptions are posted again this often (LG's docs: events expire after 1–24 h).</summary>
    public TimeSpan SubscriptionRefresh { get; init; } = TimeSpan.FromHours(24);

    /// <summary>After an MQTT reconnect, event subscriptions are posted again unless they were within this time (reconnect storms).</summary>
    public TimeSpan ResubscribeMinInterval { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>A client certificate is requested at most this often (LG throttles issuance).</summary>
    public TimeSpan CertificateMinInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>A stored certificate this close to its expiry is replaced before connecting.</summary>
    public TimeSpan CertificateRenewBefore { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Device profiles are fetched again after this long (or when a device is added or renamed).</summary>
    public TimeSpan ProfileMaxAge { get; init; } = TimeSpan.FromDays(7);

    /// <summary>After an account problem the user must fix (terms, country), the runtime tries again this often.</summary>
    public TimeSpan AccountRetry { get; init; } = TimeSpan.FromHours(1);

    /// <summary>First retry delay after a transient REST failure (doubling up to <see cref="TransientRetryMax"/>).</summary>
    public TimeSpan TransientRetryInitial { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan TransientRetryMax { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan MqttConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>MQTT keep-alive (the SDK uses 6 s; AWS IoT accepts 30–1200 s).</summary>
    public TimeSpan MqttKeepAlive { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often an established MQTT connection is checked (besides the disconnect event).</summary>
    public TimeSpan ConnectionCheckInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>A command's idempotency key is remembered this long (a repeat is rejected as a duplicate).</summary>
    public TimeSpan IdempotencyWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>A device added, removed or renamed (push/devices) triggers a rediscovery after this quiet time.</summary>
    public TimeSpan RediscoveryDebounce { get; init; } = TimeSpan.FromSeconds(30);

    public LgRateOptions Rate { get; init; } = LgRateOptions.Default;

    public LgHttpOptions Http { get; init; } = LgHttpOptions.Default;

    /// <summary>Tests only: the REST base URL instead of the country's region (never configurable by users).</summary>
    public Uri? BaseUrlOverride { get; init; }

    /// <summary>Tests only: the roots the broker certificate must chain to instead of the bundled Amazon Root CA 1.</summary>
    public X509Certificate2Collection? TrustedRootsOverride { get; init; }

    /// <summary>Tests only: a fixed local time of the nightly energy run instead of a random one in 01:00–05:59.</summary>
    public TimeSpan? EnergyTimeOfDay { get; init; }
}

/// <summary>
/// What survives a runtime restart within the process (held by the factory, per integration): poll times (the 5-minute
/// rule holds across restarts by the supervisor), last statuses, cached profiles, the last certificate request (at most one a
/// minute), the last subscription refresh and the energy run's time of day.
/// </summary>
internal sealed class LgIntegrationMemory
{
    public ConcurrentDictionary<string, DateTimeOffset> LastPoll { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, LgStatus> Statuses { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, LgCachedProfile> Profiles { get; } = new(StringComparer.Ordinal);

    /// <summary>Single-flight certificate issuance (two runs of one integration never request two certificates at once).</summary>
    public SemaphoreSlim CertificateGate { get; } = new(1, 1);

    public DateTimeOffset LastCertificateRequest { get; set; } = DateTimeOffset.MinValue;

    public DateTimeOffset LastEventSubscribe { get; set; } = DateTimeOffset.MinValue;

    public TimeSpan? EnergyTimeOfDay { get; set; }

    public LgStatus StatusOf(string deviceId) => Statuses.GetOrAdd(deviceId, _ => new LgStatus());
}

/// <summary>A device's profile and energy profile as fetched at <see cref="FetchedAtUtc"/>.</summary>
internal sealed record LgCachedProfile(System.Text.Json.JsonElement Profile, System.Text.Json.JsonElement? Energy, string Alias, DateTimeOffset FetchedAtUtc);

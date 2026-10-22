using System.Net;
using System.Net.Sockets;
using ControlHome.Domain.Devices;

namespace ControlHome.Connectors.Abstractions;

/// <summary>What kind of failure a vendor call hit, and what to do about it.</summary>
public enum ConnectorErrorKind
{
    /// <summary>Not an error.</summary>
    None,

    /// <summary>Network trouble, a timeout or a server error: retry with back-off.</summary>
    Transient,

    /// <summary>Too many requests (HTTP 429): wait <see cref="ConnectorError.RetryAfter"/> before the next call.</summary>
    RateLimited,

    /// <summary>The credentials were rejected (HTTP 401): stop and ask the user to re-authorize.</summary>
    Unauthorized,

    /// <summary>The vendor requires a paid plan (HTTP 402 or a plan error in the body): stop until the user acts.</summary>
    PlanRequired,

    /// <summary>Any other client error: a bug or an unsupported device; do not retry the same request.</summary>
    Permanent,
}

/// <summary>A classified failure.</summary>
/// <param name="Status">The integration status to set (<c>null</c> when it should not change).</param>
/// <param name="RetryAfter">How long to wait before retrying, when the vendor said so or a default applies.</param>
public sealed record ConnectorError(ConnectorErrorKind Kind, IntegrationStatus? Status, TimeSpan? RetryAfter)
{
    public static ConnectorError None { get; } = new(ConnectorErrorKind.None, null, null);

    /// <summary>Whether the same call may be retried (after <see cref="RetryAfter"/> or the caller's back-off).</summary>
    public bool Retryable => Kind is ConnectorErrorKind.Transient or ConnectorErrorKind.RateLimited;
}

/// <summary>
/// The shared mapping from vendor HTTP failures to integration states: 401 → <see cref="IntegrationStatus.NeedsReauth"/>,
/// 402 or a plan error → <see cref="IntegrationStatus.PlanRequired"/>, 429 → back off for <c>Retry-After</c> (default
/// <see cref="DefaultRateLimitBackoff"/>) with <see cref="IntegrationStatus.Degraded"/>, 408/5xx and network errors →
/// transient (<see cref="IntegrationStatus.Degraded"/>), any other 4xx → <see cref="IntegrationStatus.Error"/>.
/// </summary>
public static class ConnectorErrorClassifier
{
    /// <summary>The back-off for a 429 without a usable <c>Retry-After</c>.</summary>
    public static readonly TimeSpan DefaultRateLimitBackoff = TimeSpan.FromSeconds(60);

    /// <summary>The longest <c>Retry-After</c> honoured (a misbehaving server cannot park the integration for days).</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(1);

    /// <param name="retryAfter">The response's <c>Retry-After</c>, if any.</param>
    /// <param name="planError">The connector recognised a "paid plan required" error in the response body.</param>
    public static ConnectorError Classify(HttpStatusCode statusCode, TimeSpan? retryAfter = null, bool planError = false)
    {
        var code = (int)statusCode;
        if (planError || statusCode == HttpStatusCode.PaymentRequired)
        {
            return new ConnectorError(ConnectorErrorKind.PlanRequired, IntegrationStatus.PlanRequired, null);
        }

        return code switch
        {
            < 400 => ConnectorError.None,
            401 => new ConnectorError(ConnectorErrorKind.Unauthorized, IntegrationStatus.NeedsReauth, null),
            429 => new ConnectorError(ConnectorErrorKind.RateLimited, IntegrationStatus.Degraded, Clamp(retryAfter) ?? DefaultRateLimitBackoff),
            408 or >= 500 => new ConnectorError(ConnectorErrorKind.Transient, IntegrationStatus.Degraded, Clamp(retryAfter)),
            _ => new ConnectorError(ConnectorErrorKind.Permanent, IntegrationStatus.Error, null),
        };
    }

    /// <summary>Classifies a response, reading <c>Retry-After</c> (seconds or an HTTP date relative to <paramref name="time"/>).</summary>
    public static ConnectorError Classify(HttpResponseMessage response, TimeProvider time, bool planError = false)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(time);

        TimeSpan? retryAfter = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - time.GetUtcNow() is var wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero,
            _ => null,
        };
        return Classify(response.StatusCode, retryAfter, planError);
    }

    /// <summary>
    /// Classifies an exception from a vendor call: network failures and timeouts are transient; anything else is
    /// permanent (a bug to fix, not to retry).
    /// </summary>
    public static ConnectorError Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            HttpRequestException { StatusCode: { } status } => Classify(status),
            HttpRequestException or SocketException or IOException or TimeoutException or TaskCanceledException =>
                new ConnectorError(ConnectorErrorKind.Transient, IntegrationStatus.Degraded, null),
            _ => new ConnectorError(ConnectorErrorKind.Permanent, IntegrationStatus.Error, null),
        };
    }

    private static TimeSpan? Clamp(TimeSpan? retryAfter) =>
        retryAfter is not { } value ? null : value < TimeSpan.Zero ? TimeSpan.Zero : value > MaxRetryAfter ? MaxRetryAfter : value;
}

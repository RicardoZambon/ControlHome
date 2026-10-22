namespace ControlHome.Domain.Devices;

/// <summary>
/// The health of an <see cref="Integration"/>, set by its connector runtime (<c>IConnectorContext.SetStatus</c>) and by the
/// integration supervisor. Stored as a string.
/// </summary>
public enum IntegrationStatus
{
    /// <summary>Starting, or reconnecting after a failure.</summary>
    Connecting,

    /// <summary>Running normally.</summary>
    Ok,

    /// <summary>Running with problems (some devices unreachable, rate limited, slow responses).</summary>
    Degraded,

    /// <summary>The vendor rejected the credentials (HTTP 401): the user must run the reauth flow.</summary>
    NeedsReauth,

    /// <summary>The vendor requires a paid plan (HTTP 402 or a plan error).</summary>
    PlanRequired,

    /// <summary>A vendor quota (API calls per day/month) is used up; the runtime waits for the next period.</summary>
    QuotaExceeded,

    /// <summary>Failed; the supervisor retries with exponential back-off.</summary>
    Error,

    /// <summary>Not running: the integration is disabled, or this process is a read-only standby.</summary>
    Stopped,
}

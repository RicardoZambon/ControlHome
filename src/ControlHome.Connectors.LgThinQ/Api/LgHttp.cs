using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Timeout;

namespace ControlHome.Connectors.LgThinQ.Api;

/// <summary>Who calls the API: the request headers of one integration (the token is never logged).</summary>
/// <param name="Pat">The personal access token (<c>Authorization: Bearer</c>).</param>
/// <param name="Country">The account's country (<c>x-country</c>).</param>
/// <param name="ClientId">The integration's persistent client id (<c>x-client-id</c>).</param>
/// <param name="ApiKey">The SDK's public <c>x-api-key</c>.</param>
internal sealed record LgIdentity(string Pat, string Country, string ClientId, string ApiKey)
{
    /// <summary>Prints without the token.</summary>
    public override string ToString() => $"country {Country}, client {ClientId}";
}

/// <summary>HTTP tuning (tests shorten it).</summary>
/// <param name="AttemptTimeout">Timeout of one attempt (the SDK uses 15 s).</param>
/// <param name="GetRetries">Retries of an idempotent GET after a transient failure (never for POST/DELETE).</param>
/// <param name="RetryDelay">First retry delay (exponential, with jitter).</param>
internal sealed record LgHttpOptions(TimeSpan AttemptTimeout, int GetRetries, TimeSpan RetryDelay)
{
    public static LgHttpOptions Default { get; } = new(TimeSpan.FromSeconds(15), 2, TimeSpan.FromSeconds(2));
}

/// <summary>
/// The connector's HTTP stack: one primary handler for every integration (a pooled <see cref="SocketsHttpHandler"/>, or a
/// test handler), and per integration a pipeline of resilience (retries only for idempotent GETs, a timeout per attempt)
/// → headers (<see cref="LgHeadersHandler"/>: a fresh <c>x-message-id</c> per attempt) → call budget
/// (<see cref="LgBudgetHandler"/>: every attempt takes a token) → the primary handler.
/// </summary>
internal sealed class LgHttp : IDisposable
{
    private readonly HttpMessageInvoker _primary;
    private readonly bool _ownsPrimary;
    private readonly HttpMessageHandler _primaryHandler;

    /// <param name="primary">The handler that sends requests; <c>null</c> for a pooled <see cref="SocketsHttpHandler"/>.</param>
    public LgHttp(HttpMessageHandler? primary, LgHttpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        _ownsPrimary = primary is null;
        _primaryHandler = primary ?? new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        _primary = new HttpMessageInvoker(_primaryHandler, disposeHandler: false);
    }

    public LgHttpOptions Options { get; }

    /// <summary>A client for one integration: requests go to <paramref name="baseUrl"/> with <paramref name="identity"/>'s headers.</summary>
    public HttpClient CreateClient(Uri baseUrl, LgIdentity identity, LgRateBudget budget, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(time);
        var budgetHandler = new LgBudgetHandler(budget) { InnerHandler = new SharedHandler(_primary) };
        var headers = new LgHeadersHandler(identity) { InnerHandler = budgetHandler };
        var resilience = new ResilienceHandler(Pipeline(time)) { InnerHandler = headers };
        return new HttpClient(resilience, disposeHandler: true)
        {
            BaseAddress = baseUrl,

            // The pipeline's per-attempt timeout applies; this only bounds a whole call with its retries.
            Timeout = (Options.AttemptTimeout * (Options.GetRetries + 1)) + TimeSpan.FromMinutes(1),
        };
    }

    public void Dispose()
    {
        _primary.Dispose();
        if (_ownsPrimary)
        {
            _primaryHandler.Dispose();
        }
    }

    private ResiliencePipeline<HttpResponseMessage> Pipeline(TimeProvider time)
    {
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage> { TimeProvider = time };
        if (Options.GetRetries > 0)
        {
            builder.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = Options.GetRetries,
                Delay = Options.RetryDelay,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,

                // Only idempotent reads are retried: a control, subscription or certificate request is never sent twice.
                // 429 (and LG's 1306) are not retried at all: the budget pauses instead.
                ShouldHandle = args => ValueTask.FromResult(IsGet(args.Context) && args.Outcome switch
                {
                    { Exception: HttpRequestException or TimeoutRejectedException } => true,
                    { Result: { } response } => response.StatusCode is HttpStatusCode.RequestTimeout or >= HttpStatusCode.InternalServerError,
                    _ => false,
                }),
            });
        }

        builder.AddTimeout(Options.AttemptTimeout);
        return builder.Build();
    }

    private static bool IsGet(ResilienceContext context) => context.GetRequestMessage()?.Method == HttpMethod.Get;

    /// <summary>The innermost handler of a pipeline: forwards to the shared primary handler without owning it.</summary>
    private sealed class SharedHandler(HttpMessageInvoker primary) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            primary.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Adds the ThinQ Connect PAT headers of LG's SDK to every attempt: <c>Authorization: Bearer</c>, <c>x-country</c>,
/// <c>x-message-id</c> (22-character URL-safe base64 of a random UUID, new per attempt), <c>x-client-id</c>,
/// <c>x-api-key</c> and <c>x-service-phase: OP</c>. (LG's public docs show a partner header set — <c>x-country-code</c>,
/// <c>x-service-id</c>, <c>x-service-key</c> — that does not apply to PAT use.)
/// </summary>
internal sealed class LgHeadersHandler(LgIdentity identity) : DelegatingHandler
{
    public const string MessageIdHeader = "x-message-id";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var headers = request.Headers;
        headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.Pat);
        headers.Remove("x-country");
        headers.Remove(MessageIdHeader);
        headers.Remove("x-client-id");
        headers.Remove("x-api-key");
        headers.Remove("x-service-phase");
        headers.TryAddWithoutValidation("x-country", identity.Country);
        headers.TryAddWithoutValidation(MessageIdHeader, NewMessageId());
        headers.TryAddWithoutValidation("x-client-id", identity.ClientId);
        headers.TryAddWithoutValidation("x-api-key", identity.ApiKey);
        headers.TryAddWithoutValidation("x-service-phase", "OP");
        if (!headers.Accept.Any())
        {
            headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>A new message id like the SDK's: the 16 bytes of a random UUID in URL-safe base64 without padding (22 chars).</summary>
    public static string NewMessageId() =>
        Convert.ToBase64String(Guid.NewGuid().ToByteArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Takes a token of the integration's call budget for every attempt (retries included).</summary>
internal sealed class LgBudgetHandler(LgRateBudget budget) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await budget.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Formats LG's date parameters.</summary>
internal static class LgDates
{
    public static string Day(DateOnly date) => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    public static string Month(DateOnly date) => date.ToString("yyyyMM", CultureInfo.InvariantCulture);
}

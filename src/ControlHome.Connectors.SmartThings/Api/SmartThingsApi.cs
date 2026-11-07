using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.SmartThings.Auth;
using ControlHome.Connectors.SmartThings.Mapping;
using ControlHome.Connectors.SmartThings.Model;
using ControlHome.Domain.Devices;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace ControlHome.Connectors.SmartThings.Api;

/// <summary>A SmartThings location (one integration each).</summary>
internal sealed record SmartThingsLocation(string Id, string Name);

/// <summary>
/// The SmartThings REST API, hand-written for the dozen calls the connector makes. GETs are retried (twice, exponential
/// back-off with jitter, on network errors, timeouts and 5xx; Microsoft.Extensions.Http.Resilience's retry options on
/// the connector's clock); POSTs are sent exactly once. A 401 on a GET refreshes the OAuth token once (single-flight)
/// and repeats the GET. Every answer's <c>X-RateLimit-*</c> headers feed the bucket of that call; a 429 blocks the
/// bucket until the vendor's reset. Bearer tokens are only ever sent to the API host (pagination links elsewhere are
/// not followed).
/// </summary>
internal sealed class SmartThingsApi
{
    private readonly HttpClient _http;
    private readonly SmartThingsOptions _options;
    private readonly IAccessTokenSource _tokens;
    private readonly TimeProvider _time;
    private readonly ResiliencePipeline<HttpResponseMessage> _gets;
    private long _calls;

    public SmartThingsApi(HttpClient http, SmartThingsOptions options, IAccessTokenSource tokens, TimeProvider time, RateLimitTracker limits)
    {
        _http = http;
        _options = options;
        _tokens = tokens;
        _time = time;
        Limits = limits;
        var retry = new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = Math.Max(1, options.GetRetries),
            Delay = options.GetRetryDelay,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            ShouldHandle = static args => ValueTask.FromResult(args.Outcome switch
            {
                { Exception: HttpRequestException or TimeoutException } => true,
                { Result.StatusCode: HttpStatusCode.RequestTimeout or >= HttpStatusCode.InternalServerError } => true,
                _ => false,
            }),
        };
        _gets = options.GetRetries <= 0
            ? ResiliencePipeline<HttpResponseMessage>.Empty
            : new ResiliencePipelineBuilder<HttpResponseMessage> { TimeProvider = time }.AddRetry(retry).Build();
    }

    public RateLimitTracker Limits { get; }

    /// <summary>API calls made (diagnostics: paid tiers may meter them).</summary>
    public long Calls => Interlocked.Read(ref _calls);

    public async Task<IReadOnlyList<SmartThingsLocation>> ListLocationsAsync(CancellationToken cancellationToken)
    {
        var document = await GetAsync(RateLimitTracker.Locations, Relative("locations"), "GET /locations", cancellationToken).ConfigureAwait(false);
        return [.. document.Array("items")
            .Select(i => i.Str("locationId") is { Length: > 0 } id ? new SmartThingsLocation(id, i.Str("name") ?? id) : null)
            .OfType<SmartThingsLocation>()];
    }

    /// <summary>Room id → name.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ListRoomsAsync(string locationId, CancellationToken cancellationToken)
    {
        var document = await GetAsync(
            RateLimitTracker.Rooms,
            Relative($"locations/{Uri.EscapeDataString(locationId)}/rooms"),
            "GET /locations/{id}/rooms",
            cancellationToken).ConfigureAwait(false);
        var rooms = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in document.Array("items"))
        {
            if (item.Str("roomId") is { Length: > 0 } id && item.Str("name") is { Length: > 0 } name)
            {
                rooms[id] = name;
            }
        }

        return rooms;
    }

    /// <summary>
    /// Every device of the location with status and health (<c>includeStatus</c>, <c>includeHealth</c>,
    /// <c>includeRestricted</c>), following <c>_links.next</c>. Throws when any page fails: a partial list must never
    /// orphan the devices of the missing pages.
    /// </summary>
    public async Task<DeviceListing> ListDevicesAsync(string locationId, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        Uri? next = Relative($"devices?locationId={Uri.EscapeDataString(locationId)}&includeStatus=true&includeHealth=true&includeRestricted=true");
        var pages = 0;
        var complete = true;
        while (next is not null)
        {
            var document = await GetAsync(RateLimitTracker.Devices, next, "GET /devices", cancellationToken).ConfigureAwait(false);
            pages++;
            items.AddRange(document.Array("items").Select(i => i.Clone()));
            next = document.Get("_links").Get("next").Str("href") is { Length: > 0 } href ? SameApi(href) : null;
            if (document.Get("_links").Get("next").Str("href") is { Length: > 0 } && next is null)
            {
                // A link to another host is never followed with the bearer token.
                complete = false;
            }

            if (next is not null && pages >= _options.MaxPages)
            {
                complete = false;
                break;
            }
        }

        return new DeviceListing(items, pages, complete);
    }

    public Task<JsonElement> GetDeviceStatusAsync(string deviceId, CancellationToken cancellationToken) =>
        GetAsync(RateLimitTracker.Device(deviceId), Relative($"devices/{Uri.EscapeDataString(deviceId)}/status"), "GET /devices/{id}/status", cancellationToken);

    /// <summary>A capability definition, or <c>null</c> when SmartThings does not have one for this account (404/403).</summary>
    public async Task<CapabilityDocument?> GetCapabilityAsync(string capabilityId, int version, CancellationToken cancellationToken)
    {
        try
        {
            var document = await GetAsync(
                RateLimitTracker.Capabilities,
                Relative($"capabilities/{Uri.EscapeDataString(capabilityId)}/{version.ToString(CultureInfo.InvariantCulture)}"),
                "GET /capabilities/{id}/{version}",
                cancellationToken).ConfigureAwait(false);
            return CapabilityDocument.Parse(document);
        }
        catch (SmartThingsApiException exception) when (exception.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden && exception.Kind == ConnectorErrorKind.Permanent)
        {
            return null;
        }
    }

    /// <summary>
    /// Sends commands (at most <see cref="CommandPlanner.MaxCommandsPerRequest"/>) in ONE request, never repeated: the
    /// results' statuses (<c>ACCEPTED</c>, <c>COMPLETED</c>, <c>FAILED</c>).
    /// </summary>
    public async Task<IReadOnlyList<string>> SendCommandsAsync(string deviceId, IReadOnlyList<StCommand> commands, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(commands.Count, CommandPlanner.MaxCommandsPerRequest);
        var bucket = Limits["commands:" + deviceId];
        var now = _time.GetUtcNow();
        if (bucket.AvailableAt(now) is var available && available > now)
        {
            throw RateLimited(available - now, "POST /devices/{id}/commands");
        }

        var token = await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var body = CommandPlanner.Body(commands).ToJsonString();
        using var request = new HttpRequestMessage(HttpMethod.Post, Relative($"devices/{Uri.EscapeDataString(deviceId)}/commands"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await SendOnceAsync(request, "POST /devices/{id}/commands", cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        bucket.Observe(RateLimitHeaders.Parse(response.Headers), _time.GetUtcNow());
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized && _tokens.CanRefresh
                && await _tokens.RefreshAfterRejectionAsync(token, cancellationToken).ConfigureAwait(false) is { } fresh
                && !string.Equals(fresh, token, StringComparison.Ordinal))
            {
                // Not repeated (commands are never sent twice), but the next one goes out with the renewed token.
                throw new SmartThingsApiException(
                    new ConnectorError(ConnectorErrorKind.Transient, IntegrationStatus.Degraded, null),
                    response.StatusCode,
                    null,
                    "SmartThings had rejected the access token; it was renewed. Send the command again.");
            }

            throw Failure(bucket, response, text, "POST /devices/{id}/commands");
        }

        return Json.TryParse(text) is { } document
            ? [.. document.Array("results").Select(r => r.Str("status") ?? "UNKNOWN")]
            : [];
    }

    private async Task<JsonElement> GetAsync(string bucketName, Uri url, string operation, CancellationToken cancellationToken)
    {
        var bucket = Limits[bucketName];
        var now = _time.GetUtcNow();
        if (bucket.AvailableAt(now) is var available && available > now)
        {
            throw RateLimited(available - now, operation);
        }

        var token = await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            using var response = await GetWithRetriesAsync(url, token, operation, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            bucket.Observe(RateLimitHeaders.Parse(response.Headers), _time.GetUtcNow());
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && _tokens.CanRefresh
                && await _tokens.RefreshAfterRejectionAsync(token, cancellationToken).ConfigureAwait(false) is { } fresh
                && !string.Equals(fresh, token, StringComparison.Ordinal))
            {
                token = fresh;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw Failure(bucket, response, text, operation);
            }

            return Json.TryParse(text) ?? throw new SmartThingsApiException(
                new ConnectorError(ConnectorErrorKind.Transient, IntegrationStatus.Degraded, null),
                response.StatusCode,
                null,
                $"SmartThings answered {operation} with something that is not JSON.");
        }
    }

    private async Task<HttpResponseMessage> GetWithRetriesAsync(Uri url, string token, string operation, CancellationToken cancellationToken)
    {
        try
        {
            return await _gets.ExecuteAsync(
                async ct =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    return await SendAttemptAsync(request, ct).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException)
        {
            throw new SmartThingsApiException(ConnectorErrorClassifier.Classify(exception), null, null, $"SmartThings cannot be reached ({operation}).", exception);
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage request, string operation, CancellationToken cancellationToken)
    {
        try
        {
            return await SendAttemptAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException)
        {
            throw new SmartThingsApiException(ConnectorErrorClassifier.Classify(exception), null, null, $"SmartThings cannot be reached ({operation}).", exception);
        }
    }

    /// <summary>One request with its own timeout on the connector's clock (a timeout is a <see cref="TimeoutException"/>).</summary>
    private async Task<HttpResponseMessage> SendAttemptAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        using var timeout = new CancellationTokenSource(_options.RequestTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("SmartThings did not answer in time.", exception);
        }
    }

    private SmartThingsApiException Failure(RateBudget bucket, HttpResponseMessage response, string text, string operation)
    {
        var failure = SmartThingsErrors.From(response, text, _time, operation);
        if (failure.Kind == ConnectorErrorKind.RateLimited)
        {
            // Retry-After when given, else the window's reset, else the classifier's default.
            var wait = response.Headers.RetryAfter is not null ? failure.Error.RetryAfter : bucket.ResetAfter(_time.GetUtcNow()) ?? failure.Error.RetryAfter;
            wait ??= ConnectorErrorClassifier.DefaultRateLimitBackoff;
            bucket.Block(_time.GetUtcNow() + wait.Value);
            return RateLimited(wait.Value, operation);
        }

        return failure;
    }

    private static SmartThingsApiException RateLimited(TimeSpan wait, string operation) =>
        new(
            new ConnectorError(ConnectorErrorKind.RateLimited, IntegrationStatus.Degraded, wait),
            HttpStatusCode.TooManyRequests,
            null,
            string.Create(CultureInfo.InvariantCulture, $"SmartThings rate limit for {operation}: waiting {Math.Ceiling(wait.TotalSeconds)} s."));

    private Uri Relative(string path) => new(_options.ApiBaseUrl, path);

    /// <summary>A pagination link, only when it points at the API itself.</summary>
    private Uri? SameApi(string href)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var url))
        {
            return Uri.TryCreate(_options.ApiBaseUrl, href, out var relative) ? SameApi(relative.AbsoluteUri) : null;
        }

        var api = _options.ApiBaseUrl;
        return url.Scheme == api.Scheme
            && string.Equals(url.Host, api.Host, StringComparison.OrdinalIgnoreCase)
            && url.Port == api.Port
            && string.IsNullOrEmpty(url.UserInfo)
            && url.AbsolutePath.StartsWith(api.AbsolutePath, StringComparison.Ordinal)
                ? url
                : null;
    }
}

/// <summary>The devices of one listing.</summary>
/// <param name="Complete">Every page was read (only then may missing devices be orphaned).</param>
internal sealed record DeviceListing(IReadOnlyList<JsonElement> Items, int Pages, bool Complete);

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Polly.Timeout;

namespace ControlHome.Connectors.LgThinQ.Api;

/// <summary>
/// The ThinQ Connect REST API (port of pythinqconnect <c>thinq_api.py</c>): devices, profiles, state, control, energy,
/// MQTT client registration and certificate, route, push and event subscriptions. Successful answers carry their payload
/// in <c>response</c>; errors in <c>error.code</c> / <c>error.message</c> (thrown as <see cref="LgApiException"/>; a 1306
/// pauses the token's budget for an hour). Never logs the token.
/// </summary>
internal sealed partial class LgApiClient(HttpClient http, LgRateBudget budget, ILogger logger)
{
    /// <summary>The MQTT client registration body of the SDK (<c>CLIENT_BODY</c>).</summary>
    public static readonly JsonObject ClientBody = new()
    {
        ["type"] = "MQTT",
        ["service-code"] = "SVC202",
        ["device-type"] = "607",
        ["allowExist"] = true,
    };

    /// <summary>The largest response read (a profile of a big appliance is a few tens of KB).</summary>
    private const int MaxResponseBytes = 4 * 1024 * 1024;

    public LgRateBudget Budget => budget;

    /// <summary><c>GET devices</c>: <c>[{deviceId, deviceInfo: {deviceType, modelName, alias, reportable, groupId?}}]</c>.</summary>
    public Task<JsonElement> GetDevicesAsync(CancellationToken cancellationToken) => SendAsync(HttpMethod.Get, "devices", null, false, cancellationToken);

    public Task<JsonElement> GetProfileAsync(string deviceId, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, $"devices/{Escape(deviceId)}/profile", null, false, cancellationToken);

    public Task<JsonElement> GetStateAsync(string deviceId, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, $"devices/{Escape(deviceId)}/state", null, false, cancellationToken);

    /// <summary><c>POST devices/{id}/control</c> with <c>x-conditional-control: true</c>; payload <c>{resource: {property: value}}</c>.</summary>
    public Task<JsonElement> ControlAsync(string deviceId, JsonObject payload, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, $"devices/{Escape(deviceId)}/control", payload, true, cancellationToken);

    public Task<JsonElement> GetEnergyProfileAsync(string deviceId, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, $"devices/energy/{Escape(deviceId)}/profile", null, false, cancellationToken);

    /// <summary><c>GET devices/energy/{id}/usage</c>: <paramref name="period"/> <c>DAILY</c> (dates <c>yyyyMMdd</c>) or <c>MONTHLY</c> (<c>yyyyMM</c>).</summary>
    public Task<JsonElement> GetEnergyUsageAsync(
        string deviceId,
        string property,
        string period,
        string startDate,
        string endDate,
        CancellationToken cancellationToken) =>
        SendAsync(
            HttpMethod.Get,
            $"devices/energy/{Escape(deviceId)}/usage?property={Uri.EscapeDataString(property)}&period={period}&startDate={startDate}&endDate={endDate}",
            null,
            false,
            cancellationToken);

    /// <summary><c>POST client</c> (<c>allowExist</c>: idempotent, done on every start like the SDK).</summary>
    public Task<JsonElement> RegisterClientAsync(CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "client", ClientBody.DeepClone(), false, cancellationToken);

    /// <summary><c>DELETE client</c>: only when the integration is removed.</summary>
    public Task<JsonElement> DeleteClientAsync(CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, "client", ClientBody.DeepClone(), false, cancellationToken);

    /// <summary><c>POST client/certificate</c> with the CSR (base64 DER, no PEM armour): <c>{result: {certificatePem, subscriptions}}</c>.</summary>
    public Task<JsonElement> IssueCertificateAsync(string csr, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "client/certificate", new JsonObject { ["service-code"] = "SVC202", ["csr"] = csr }, false, cancellationToken);

    /// <summary><c>GET route</c>: <c>{mqttServer: "mqtts://host:8883", ...}</c>.</summary>
    public Task<JsonElement> GetRouteAsync(CancellationToken cancellationToken) => SendAsync(HttpMethod.Get, "route", null, false, cancellationToken);

    /// <summary><c>POST push/{id}/subscribe</c> (device notifications); 1207 (already subscribed) counts as success.</summary>
    public Task SubscribePushAsync(string deviceId, CancellationToken cancellationToken) =>
        SubscribeAsync($"push/{Escape(deviceId)}/subscribe", null, cancellationToken);

    /// <summary>
    /// <c>POST event/{id}/subscribe</c> (state reports) with the SDK's body (<c>expire HOUR 4464</c>; LG's docs say 1–24 h,
    /// so the runtime refreshes daily); 1207 counts as success.
    /// </summary>
    public Task SubscribeEventAsync(string deviceId, CancellationToken cancellationToken) =>
        SubscribeAsync(
            $"event/{Escape(deviceId)}/subscribe",
            new JsonObject { ["expire"] = new JsonObject { ["unit"] = "HOUR", ["timer"] = 4464 } },
            cancellationToken);

    /// <summary><c>POST push/devices</c>: notifications when devices are added, removed or renamed; 1207 counts as success.</summary>
    public Task SubscribePushDevicesAsync(CancellationToken cancellationToken) => SubscribeAsync("push/devices", null, cancellationToken);

    private async Task SubscribeAsync(string path, JsonObject? body, CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync(HttpMethod.Post, path, body, false, cancellationToken).ConfigureAwait(false);
        }
        catch (LgApiException exception) when (exception.Kind == LgErrorKind.AlreadySubscribed)
        {
            // Already subscribed: the goal.
        }
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, JsonNode? body, bool control, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        if (control)
        {
            request.Headers.TryAddWithoutValidation("x-conditional-control", "true");
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException or TaskCanceledException or IOException)
        {
            LogUnreachable(logger, method.Method, Template(path), exception.GetType().Name);
            throw new LgApiException(exception);
        }

        using (response)
        {
            var json = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                LogCall(logger, method.Method, Template(path), (int)response.StatusCode);
                return json is { ValueKind: JsonValueKind.Object } payload && payload.TryGetProperty("response", out var inner)
                    ? inner.Clone()
                    : json?.Clone() ?? JsonSerializer.SerializeToElement(new JsonObject());
            }

            var code = json is { ValueKind: JsonValueKind.Object } error && error.TryGetProperty("error", out var details)
                && details.ValueKind == JsonValueKind.Object && details.TryGetProperty("code", out var value)
                    ? value.ValueKind switch
                    {
                        JsonValueKind.String => value.GetString(),
                        JsonValueKind.Number => value.GetRawText(),
                        _ => null,
                    }
                    : null;
            var retryAfter = response.Headers.RetryAfter?.Delta;
            var exception = new LgApiException(code, response.StatusCode, retryAfter);
            LogFailed(logger, method.Method, Template(path), (int)response.StatusCode, LgErrors.NameOf(code), code ?? "-");
            if (exception.Kind == LgErrorKind.QuotaExceeded)
            {
                var until = budget.Block(retryAfter);
                LogQuotaExceeded(logger, budget.CallsLastHour, until);
            }

            throw exception;
        }
    }

    private static async Task<JsonElement?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            return null;
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0 || bytes.Length > MaxResponseBytes)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Escape(string deviceId) => Uri.EscapeDataString(deviceId);

    /// <summary>The path without device ids and query values, for logs (e.g. <c>devices/{id}/state</c>).</summary>
    private static string Template(string path)
    {
        var withoutQuery = path.Split('?', 2)[0];
        var parts = withoutQuery.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0 && parts[i - 1] is "devices" or "energy" or "push" or "event" && parts[i] is not ("energy" or "devices" or "subscribe"))
            {
                parts[i] = "{id}";
            }
        }

        return string.Join('/', parts);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "LG ThinQ {Method} {Path}: {Status}")]
    private static partial void LogCall(ILogger logger, string method, string path, int status);

    [LoggerMessage(Level = LogLevel.Information, Message = "LG ThinQ {Method} {Path} failed: HTTP {Status} {ErrorName} ({Code})")]
    private static partial void LogFailed(ILogger logger, string method, string path, int status, string errorName, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "LG ThinQ {Method} {Path}: not reachable ({Error})")]
    private static partial void LogUnreachable(ILogger logger, string method, string path, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "LG ThinQ call limit exceeded (1306) after {Calls} calls in the last hour; calls paused until {Until}")]
    private static partial void LogQuotaExceeded(ILogger logger, int calls, DateTimeOffset until);
}

/// <summary>Lenient JSON accessors for vendor payloads (wrong types read as absent, never throw).</summary>
internal static class LgJson
{
    public static JsonElement? Get(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    public static string? Str(this JsonElement element, string name) =>
        element.Get(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public static double? Num(this JsonElement element, string name) =>
        element.Get(name) is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;

    public static bool? Bool(this JsonElement element, string name) =>
        element.Get(name) is { ValueKind: JsonValueKind.True or JsonValueKind.False } value ? value.GetBoolean() : null;

    public static IEnumerable<JsonElement> Array(this JsonElement element, string name) =>
        element.Get(name) is { ValueKind: JsonValueKind.Array } value ? value.EnumerateArray() : [];

    public static IEnumerable<JsonElement> Items(this JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : [];

    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

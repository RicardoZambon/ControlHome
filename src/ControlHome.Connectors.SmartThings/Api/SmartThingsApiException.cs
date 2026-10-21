using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Devices;

namespace ControlHome.Connectors.SmartThings.Api;

/// <summary>
/// A failed SmartThings call, classified (<see cref="ConnectorErrorClassifier"/>): 401 → re-authorize, 402 or a 403 whose
/// body talks about a plan → <see cref="IntegrationStatus.PlanRequired"/>, 429 → wait, 5xx/network → transient. The
/// message never contains tokens, secrets or query strings.
/// </summary>
internal sealed class SmartThingsApiException : Exception
{
    public SmartThingsApiException(ConnectorError error, HttpStatusCode? status, string? vendorCode, string message, Exception? inner = null)
        : base(message, inner)
    {
        Error = error;
        Status = status;
        VendorCode = vendorCode;
    }

    public ConnectorError Error { get; }

    public HttpStatusCode? Status { get; }

    /// <summary>The <c>error.code</c> (API) or <c>error</c> (OAuth, e.g. <c>invalid_grant</c>) of the response body.</summary>
    public string? VendorCode { get; }

    public ConnectorErrorKind Kind => Error.Kind;
}

/// <summary>Reads SmartThings error bodies (<c>{"requestId":…,"error":{"code","message"}}</c> or OAuth's <c>{"error","error_description"}</c>).</summary>
internal static partial class SmartThingsErrors
{
    /// <summary>
    /// Whether a 403 means "a paid plan is required". Nobody knows yet what the paywall answers, so this is defensive:
    /// a body that mentions a plan, subscription, billing or payment.
    /// </summary>
    public static bool IsPlanError(HttpStatusCode status, string? body) =>
        status == HttpStatusCode.PaymentRequired
        || (status == HttpStatusCode.Forbidden && body is not null && PlanWords().IsMatch(body));

    /// <summary>The vendor error code in <paramref name="body"/>, if any.</summary>
    public static string? CodeOf(string? body)
    {
        if (Json.TryParse(body) is not { ValueKind: JsonValueKind.Object } document)
        {
            return null;
        }

        return document.Get("error") switch
        {
            { ValueKind: JsonValueKind.Object } error => error.Str("code"),
            { ValueKind: JsonValueKind.String } code => code.GetString(),
            _ => null,
        };
    }

    /// <summary>A classified exception for a failed response (the body is only inspected, never quoted).</summary>
    public static SmartThingsApiException From(HttpResponseMessage response, string? body, TimeProvider time, string operation)
    {
        var plan = IsPlanError(response.StatusCode, body);
        var error = ConnectorErrorClassifier.Classify(response, time, plan);
        var code = CodeOf(body);
        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"SmartThings answered {(int)response.StatusCode} ({response.StatusCode}) to {operation}{(code is null ? "" : $" ({Sanitize(code)})")}.");
        return new SmartThingsApiException(error, response.StatusCode, code, message);
    }

    /// <summary>A vendor code made safe for messages: letters, digits, <c>_ . -</c>, at most 64 characters.</summary>
    public static string Sanitize(string code)
    {
        var clean = new string([.. code.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-')]);
        return clean.Length <= 64 ? clean : clean[..64];
    }

    [GeneratedRegex(@"\b(plan|plans|subscription|subscribe|billing|payment|paid|pricing|tier)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlanWords();
}

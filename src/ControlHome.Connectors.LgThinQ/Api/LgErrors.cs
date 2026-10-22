using System.Net;
using ControlHome.Connectors.Abstractions;

namespace ControlHome.Connectors.LgThinQ.Api;

/// <summary>What an LG ThinQ API error means for the integration.</summary>
internal enum LgErrorKind
{
    /// <summary>The personal access token was rejected (1103, 1218, 1302, HTTP 401): ask the user for a new one.</summary>
    NeedsReauth,

    /// <summary>The account's hourly call limit is used up (1306): no call for an hour.</summary>
    QuotaExceeded,

    /// <summary>The account must be fixed by the user (terms not accepted, wrong country, unknown user): retry rarely.</summary>
    Account,

    /// <summary>A command the device refused (remote control off, wrong state, powered off, ...).</summary>
    CommandRejected,

    /// <summary>The device is gone, not owned, or does not support the call: skip it.</summary>
    Device,

    /// <summary>1207: already subscribed — a success for subscribe calls.</summary>
    AlreadySubscribed,

    /// <summary>A server error, a timeout or a network failure: retry with back-off.</summary>
    Transient,

    /// <summary>Anything else (a bad request, an unknown code).</summary>
    Other,
}

/// <summary>
/// The ThinQ Connect API error codes (pythinqconnect <c>ThinQAPIErrorCodes</c>) and how ControlHome treats them: integration
/// statuses for account-level errors, user-readable command rejections (in English, for logs and the audit trail) for
/// control errors.
/// </summary>
internal static class LgErrors
{
    public const string InvalidToken = "1103";
    public const string InvalidTokenAgain = "1218";
    public const string NotFoundToken = "1302";
    public const string InvalidServiceKey = "1301";
    public const string AlreadySubscribedPush = "1207";
    public const string NotAcceptableTerms = "1304";
    public const string ExceededApiCalls = "1306";
    public const string NotSupportedCountry = "1307";
    public const string NoControlAuthority = "1308";
    public const string NotAllowedApiAgain = "1309";
    public const string InvalidCommand = "2207";
    public const string RemoteControlOff = "2301";

    private static readonly Dictionary<string, string> _names = new(StringComparer.Ordinal)
    {
        ["0000"] = "UNKNOWN_ERROR", ["1000"] = "BAD_REQUEST", ["1101"] = "MISSING_PARAMETERS", ["1102"] = "UNACCEPTABLE_PARAMETERS",
        ["1103"] = "INVALID_TOKEN", ["1104"] = "INVALID_MESSAGE_ID", ["1201"] = "NOT_REGISTERED_ADMIN", ["1202"] = "NOT_REGISTERED_USER",
        ["1203"] = "NOT_REGISTERED_SERVICE", ["1204"] = "NOT_SUBSCRIBED_EVENT", ["1205"] = "NOT_EXIST_DEVICE", ["1206"] = "NOT_SUBSCRIBED_PUSH",
        ["1207"] = "ALREADY_SUBSCRIBED_PUSH", ["1208"] = "NOT_REGISTERED_SERVICE_BY_ADMIN", ["1209"] = "NOT_REGISTERED_USER_IN_SERVICE",
        ["1210"] = "NOT_REGISTERED_DEVICE_IN_SERVICE", ["1211"] = "NOT_REGISTERED_DEVICE_BY_USER", ["1212"] = "NOT_OWNED_DEVICE",
        ["1213"] = "NOT_REGISTERED_DEVICE", ["1214"] = "NOT_SUBSCRIBABLE_DEVICE", ["1216"] = "INCORRECT_HEADER", ["1217"] = "ALREADY_DEVICE_DELETED",
        ["1218"] = "INVALID_TOKEN_AGAIN", ["1219"] = "NOT_SUPPORTED_MODEL", ["1220"] = "NOT_SUPPORTED_FEATURE", ["1221"] = "NOT_SUPPORTED_PRODUCT",
        ["1222"] = "NOT_CONNECTED_DEVICE", ["1223"] = "INVALID_STATUS_DEVICE", ["1224"] = "INVALID_DEVICE_ID", ["1225"] = "DUPLICATE_DEVICE_ID",
        ["1301"] = "INVALID_SERVICE_KEY", ["1302"] = "NOT_FOUND_TOKEN", ["1303"] = "NOT_FOUND_USER", ["1304"] = "NOT_ACCEPTABLE_TERMS",
        ["1305"] = "NOT_ALLOWED_API", ["1306"] = "EXCEEDED_API_CALLS", ["1307"] = "NOT_SUPPORTED_COUNTRY", ["1308"] = "NO_CONTROL_AUTHORITY",
        ["1309"] = "NOT_ALLOWED_API_AGAIN", ["1310"] = "NOT_SUPPORTED_DOMAIN", ["1311"] = "BAD_REQUEST_FORMAT",
        ["1312"] = "EXCEEDED_NUMBER_OF_EVENT_SUBSCRIPTION", ["2000"] = "INTERNAL_SERVER_ERROR", ["2101"] = "NOT_SUPPORTED_MODEL_AGAIN",
        ["2201"] = "NOT_PROVIDED_FEATURE", ["2202"] = "NOT_SUPPORTED_PRODUCT_AGAIN", ["2203"] = "NOT_EXISTENT_MODEL_JSON",
        ["2205"] = "INVALID_DEVICE_STATUS", ["2207"] = "INVALID_COMMAND_ERROR", ["2208"] = "FAIL_DEVICE_CONTROL", ["2209"] = "DEVICE_RESPONSE_DELAY",
        ["2210"] = "RETRY_REQUEST", ["2212"] = "SYNCING", ["2213"] = "RETRY_AFTER_DELETING_DEVICE", ["2214"] = "FAIL_REQUEST",
        ["2301"] = "COMMAND_NOT_SUPPORTED_IN_REMOTE_OFF", ["2302"] = "COMMAND_NOT_SUPPORTED_IN_STATE", ["2303"] = "COMMAND_NOT_SUPPORTED_IN_ERROR",
        ["2304"] = "COMMAND_NOT_SUPPORTED_IN_POWER_OFF", ["2305"] = "COMMAND_NOT_SUPPORTED_IN_MODE",
    };

    /// <summary>The SDK's name of an error code (<c>UNKNOWN_ERROR</c> for unknown codes).</summary>
    public static string NameOf(string? code) => code is not null && _names.TryGetValue(code, out var name) ? name : "UNKNOWN_ERROR";

    /// <summary>The kind of an error: by its code, else by the HTTP status.</summary>
    public static LgErrorKind Classify(string? code, HttpStatusCode? status) => code switch
    {
        InvalidToken or InvalidTokenAgain or NotFoundToken => LgErrorKind.NeedsReauth,
        ExceededApiCalls => LgErrorKind.QuotaExceeded,
        NotAcceptableTerms or NotSupportedCountry or "1202" or "1209" or "1303" or "1305" or "1301" or "1216" or "1310" => LgErrorKind.Account,
        AlreadySubscribedPush => LgErrorKind.AlreadySubscribed,
        NoControlAuthority or NotAllowedApiAgain or InvalidCommand or "2205" or "2208" or "1222" or "1223"
            or "2301" or "2302" or "2303" or "2304" or "2305" => LgErrorKind.CommandRejected,
        "1205" or "1211" or "1212" or "1213" or "1214" or "1217" or "1219" or "1220" or "1221" or "1224"
            or "2101" or "2201" or "2202" or "2203" => LgErrorKind.Device,
        "0000" or "2000" or "2209" or "2210" or "2212" or "2214" => LgErrorKind.Transient,
        _ => status switch
        {
            HttpStatusCode.Unauthorized => LgErrorKind.NeedsReauth,
            HttpStatusCode.TooManyRequests => LgErrorKind.QuotaExceeded,
            HttpStatusCode.RequestTimeout or >= HttpStatusCode.InternalServerError => LgErrorKind.Transient,
            _ => LgErrorKind.Other,
        },
    };

    /// <summary>The command rejection (a <see cref="CommandRejectionCodes"/> value and an English explanation) for a control error.</summary>
    public static CommandOutcome Rejection(LgApiException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var code = error.Code;
        var (rejection, message) = code switch
        {
            RemoteControlOff => (CommandRejectionCodes.Refused, "Remote control is off on the appliance: turn on Remote Start on the appliance itself and try again."),
            "2302" => (CommandRejectionCodes.Refused, "The appliance cannot do this in its current state."),
            "2303" => (CommandRejectionCodes.Refused, "The appliance reports an error; check it on site."),
            "2304" => (CommandRejectionCodes.Refused, "The appliance is powered off; turn it on first."),
            "2305" => (CommandRejectionCodes.Refused, "This is not possible in the appliance's current mode."),
            NoControlAuthority => (CommandRejectionCodes.Refused, "This LG account is not allowed to control the appliance (check the token's scopes)."),
            NotAllowedApiAgain => (CommandRejectionCodes.Refused, "LG does not allow this command for the appliance."),
            InvalidCommand => (CommandRejectionCodes.InvalidArguments, "LG rejected the command: the appliance needs other settings, or several settings in one command."),
            "2205" or "1223" => (CommandRejectionCodes.Refused, "The appliance's state does not allow this command right now."),
            "2208" => (CommandRejectionCodes.Refused, "The appliance did not accept the command."),
            "1222" => (CommandRejectionCodes.Unavailable, "The appliance is not connected to LG ThinQ (check its Wi-Fi)."),
            "2209" => (CommandRejectionCodes.Unavailable, "The appliance did not answer LG ThinQ in time; check its state before trying again."),
            _ => error.Kind switch
            {
                LgErrorKind.NeedsReauth => (CommandRejectionCodes.Unavailable, "LG ThinQ rejected the access token; replace it in the integration."),
                LgErrorKind.QuotaExceeded => (CommandRejectionCodes.Unavailable, "The LG ThinQ call limit is used up; try again in an hour."),
                LgErrorKind.Device => (CommandRejectionCodes.UnsupportedCommand, "LG ThinQ does not support this command for the appliance."),
                LgErrorKind.Transient => (CommandRejectionCodes.Unavailable, "LG ThinQ is not reachable right now."),
                _ => (CommandRejectionCodes.Refused, "LG ThinQ refused the command."),
            },
        };
        return CommandOutcome.Reject(rejection, $"{message} (LG {NameOf(code)} {code})".Replace(" ()", string.Empty, StringComparison.Ordinal));
    }

    /// <summary>An English status message for an account-level error (no secrets, no vendor text).</summary>
    public static string StatusMessage(LgApiException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Code switch
        {
            InvalidToken or InvalidTokenAgain or NotFoundToken =>
                "LG ThinQ rejected the personal access token. Create a new token at connect-pat.lgthinq.com and replace it in the integration.",
            NotAcceptableTerms => "The LG account has not accepted LG ThinQ's terms. Open the LG ThinQ app, accept them, and wait for the next retry.",
            NotSupportedCountry => "LG ThinQ does not serve this country for the account. Pick the country the LG account was created in (replace the token).",
            ExceededApiCalls => "The LG ThinQ call limit of the account is used up; ControlHome pauses its calls for an hour.",
            "1202" or "1209" or "1303" => "LG ThinQ does not know this account in the chosen country. Check the country (replace the token).",
            InvalidServiceKey => "LG ThinQ rejected the API key (x-api-key). If LG published a new key for its SDK, enter it in the integration's advanced settings.",
            _ when error.Kind == LgErrorKind.NeedsReauth => "LG ThinQ rejected the access token. Replace it in the integration.",
            _ => $"LG ThinQ answered {NameOf(error.Code)} ({error.Code}).",
        };
    }
}

/// <summary>An LG ThinQ API call failed. The message never contains the token or a vendor's free text beyond its error name.</summary>
internal sealed class LgApiException : Exception
{
    public LgApiException(string? code, HttpStatusCode? status, TimeSpan? retryAfter = null, Exception? inner = null)
        : base(Describe(code, status), inner)
    {
        Code = code;
        Status = status;
        RetryAfter = retryAfter;
        Kind = LgErrors.Classify(code, status);
    }

    /// <summary>A network failure or timeout (no response).</summary>
    public LgApiException(Exception inner)
        : base($"LG ThinQ could not be reached ({inner?.GetType().Name}).", inner)
    {
        Kind = LgErrorKind.Transient;
    }

    /// <summary>The API's error code (e.g. <c>1103</c>), or <c>null</c> when the response had none.</summary>
    public string? Code { get; }

    public HttpStatusCode? Status { get; }

    public TimeSpan? RetryAfter { get; }

    public LgErrorKind Kind { get; }

    private static string Describe(string? code, HttpStatusCode? status) =>
        code is { Length: > 0 }
            ? $"LG ThinQ API error {LgErrors.NameOf(code)} ({code}, HTTP {(int?)status})."
            : $"LG ThinQ API error (HTTP {(int?)status}).";
}

/// <summary>A call was not made: the account's call budget is blocked after a 1306 until <see cref="Until"/>.</summary>
internal sealed class LgQuotaBlockedException(DateTimeOffset until)
    : Exception($"LG ThinQ calls are paused until {until:u} (call limit reached).")
{
    public DateTimeOffset Until { get; } = until;
}

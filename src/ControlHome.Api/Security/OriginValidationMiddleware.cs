using System.Diagnostics.CodeAnalysis;
using ControlHome.Api.Http;
using ControlHome.Application.Common;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace ControlHome.Api.Security;

/// <summary>
/// Cross-origin protection without CORS. Every mutation (any method but GET/HEAD/OPTIONS) and every WebSocket upgrade
/// must carry <c>Origin</c> equal to <c>{scheme}://{host}</c> of the request (after forwarded headers), else 403
/// <c>request.cross_origin</c>. Mutations other than WebSocket handshakes (browsers cannot add headers to those) also need
/// <c>X-ControlHome: 1</c>, else 403 <c>request.missing_header</c>. Inbound hooks (<c>/api/hooks/*</c>) are exempt.
/// </summary>
internal sealed class OriginValidationMiddleware(RequestDelegate next)
{
    public const string MarkerHeaderName = "X-ControlHome";
    public const string MarkerHeaderValue = "1";

    private static readonly PathString _hooksPath = new("/api/hooks");

    public Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var isWebSocket = IsWebSocketHandshake(context);
        if ((!isWebSocket && IsSafeMethod(request.Method)) || request.Path.StartsWithSegments(_hooksPath, StringComparison.OrdinalIgnoreCase))
        {
            return next(context);
        }

        if (!IsSameOrigin(request.Headers.Origin, request.Scheme, request.Host))
        {
            return Deny(context, ErrorCodes.RequestCrossOrigin, "The request's Origin is not this app.");
        }

        if (!isWebSocket && !HasMarkerHeader(request))
        {
            return Deny(context, ErrorCodes.RequestMissingHeader, $"The header '{MarkerHeaderName}: {MarkerHeaderValue}' is required.");
        }

        return next(context);
    }

    /// <summary>
    /// True when <paramref name="origin"/> is exactly one serialized origin (<c>scheme://host[:port]</c>, http or https)
    /// equal to <c>{scheme}://{host}</c>; host names compare case-insensitively and default ports are implied.
    /// </summary>
    internal static bool IsSameOrigin(StringValues origin, string scheme, HostString host)
    {
        if (origin.Count != 1 || !host.HasValue
            || !TryNormalizeOrigin(origin[0], out var actual)
            || !TryNormalizeOrigin($"{scheme}://{host.Value}", out var expected))
        {
            return false;
        }

        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeOrigin(string? value, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        var authorityStart = value?.IndexOf("://", StringComparison.Ordinal) ?? -1;
        if (value is null || authorityStart <= 0)
        {
            return false;
        }

        // A serialized origin ends with the authority: no path (not even "/"), query, fragment or user info.
        if (value.AsSpan(authorityStart + 3).IndexOfAny("/?#@\\") >= 0
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        // Scheme, host and port; the port is omitted when it is the scheme's default.
        normalized = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        return true;
    }

    private static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    /// <summary>An HTTP/1.1 <c>GET</c> with <c>Upgrade: websocket</c>, or an HTTP/2+ extended CONNECT for <c>websocket</c>.</summary>
    private static bool IsWebSocketHandshake(HttpContext context)
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            return true;
        }

        var request = context.Request;
        if (HttpMethods.IsGet(request.Method))
        {
            foreach (var value in request.Headers.Upgrade)
            {
                foreach (var token in (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries))
                {
                    if (string.Equals(token, "websocket", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        return HttpMethods.IsConnect(request.Method)
            && context.Features.Get<IHttpExtendedConnectFeature>() is { IsExtendedConnect: true } connect
            && string.Equals(connect.Protocol, "websocket", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasMarkerHeader(HttpRequest request) =>
        request.Headers.TryGetValue(MarkerHeaderName, out var values)
        && values.Count == 1
        && string.Equals(values[0], MarkerHeaderValue, StringComparison.Ordinal);

    private static Task Deny(HttpContext context, string code, string message) =>
        Problems.From(AppError.Forbidden(code, message)).ExecuteAsync(context);
}

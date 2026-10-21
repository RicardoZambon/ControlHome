using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ControlHome.Connectors.SmartThings.Auth;

/// <summary>What the user pasted back from the redirect page.</summary>
/// <param name="Error">The OAuth <c>error</c> (e.g. <c>access_denied</c>), if SmartThings refused.</param>
internal sealed record PastedRedirect(string? Code, string? State, string? Error)
{
    public override string ToString() => $"PastedRedirect(state {(State is null ? "missing" : "present")}, {(Code is null ? "no code" : "code")}, {Error ?? "no error"})";
}

/// <summary>
/// The OAuth authorization-code request: the authorize URL with a random <c>state</c>, and the redirected URL the user
/// pastes back (its <c>state</c> must match: CSRF protection — a pasted URL from someone else's sign-in is refused).
/// </summary>
internal static class AuthorizationRequest
{
    /// <summary>A fresh <c>state</c>: 256 random bits, URL-safe base64 (43 characters).</summary>
    public static string NewState() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static Uri BuildUrl(OAuthEndpoints endpoints, string clientId, string redirectUri, IReadOnlyList<string> scopes, string state)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var query = new StringBuilder();
        Append(query, "client_id", clientId);
        Append(query, "response_type", "code");
        Append(query, "redirect_uri", redirectUri);
        Append(query, "scope", string.Join(' ', scopes));
        Append(query, "state", state);
        var builder = new UriBuilder(endpoints.AuthorizeUrl);
        var existing = builder.Query.TrimStart('?');
        builder.Query = existing.Length == 0 ? query.ToString() : existing + "&" + query;
        return builder.Uri;
    }

    /// <summary>
    /// Reads the code and state from what the user pasted: the redirected URL from the address bar (query or fragment),
    /// just its query string, or the JSON page httpbin.org shows (<c>{"args":{"code":…,"state":…},"url":…}</c>).
    /// <c>null</c> when it holds neither a code nor an error.
    /// </summary>
    public static PastedRedirect? Parse(string? pasted)
    {
        var text = pasted?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 8192)
        {
            return null;
        }

        if (text.StartsWith('{') && Json.TryParse(text) is { ValueKind: JsonValueKind.Object } page)
        {
            if (page.Get("args") is { ValueKind: JsonValueKind.Object } args && (args.Str("code") is not null || args.Str("error") is not null))
            {
                return new PastedRedirect(args.Str("code"), args.Str("state"), args.Str("error"));
            }

            text = page.Str("url") ?? string.Empty;
        }

        var start = text.IndexOfAny(['?', '#']);
        var query = start >= 0 ? text[(start + 1)..] : text.Contains('=', StringComparison.Ordinal) ? text : string.Empty;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split(['&', '#', '?'], StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            var key = Unescape(pair[..equals]);
            if (key is "code" or "state" or "error")
            {
                values.TryAdd(key, Unescape(pair[(equals + 1)..]));
            }
        }

        var code = values.GetValueOrDefault("code");
        var error = values.GetValueOrDefault("error");
        return code is { Length: > 0 } || error is { Length: > 0 }
            ? new PastedRedirect(code is { Length: > 0 } ? code : null, values.GetValueOrDefault("state"), error is { Length: > 0 } ? error : null)
            : null;
    }

    /// <summary>Whether the pasted <paramref name="actual"/> state is the one this flow issued (constant-time compare).</summary>
    public static bool StateMatches(string expected, string? actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (actual is null)
        {
            return false;
        }

        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(actual);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static void Append(StringBuilder query, string key, string value)
    {
        if (query.Length > 0)
        {
            query.Append('&');
        }

        query.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
    }

    private static string Unescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return value;
        }
    }
}

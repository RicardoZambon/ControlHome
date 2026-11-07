using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ControlHome.Connectors.DahuaHttp.Protocol;

/// <summary>
/// An HTTP Digest challenge (<c>WWW-Authenticate: Digest …</c>, RFC 7616 / RFC 2617) and the <c>Authorization</c> that
/// answers it, for the requests that cannot go through <see cref="System.Net.Http.SocketsHttpHandler"/>'s own Digest
/// support (the at-most-once door release, <see cref="OneShotHttp"/>). Algorithms <c>MD5</c> (the default; what devices
/// in "Compatibility" mode offer), <c>MD5-sess</c>, <c>SHA-256</c> and <c>SHA-256-sess</c>; quality of protection
/// <c>auth</c> or none.
/// </summary>
internal sealed class DigestChallenge
{
    private DigestChallenge(string realm, string nonce, string? opaque, string algorithm, bool qopAuth)
    {
        Realm = realm;
        Nonce = nonce;
        Opaque = opaque;
        Algorithm = algorithm;
        QopAuth = qopAuth;
    }

    public string Realm { get; }

    public string Nonce { get; }

    public string? Opaque { get; }

    /// <summary>As the challenge spelled it (<c>MD5</c> when it named none).</summary>
    public string Algorithm { get; }

    /// <summary>The challenge offers <c>qop=auth</c> (else the RFC 2069 form without qop is used).</summary>
    public bool QopAuth { get; }

    /// <summary>The first supported Digest challenge among the <c>WWW-Authenticate</c> values, or <c>null</c>.</summary>
    public static DigestChallenge? Select(IEnumerable<string> wwwAuthenticate)
    {
        ArgumentNullException.ThrowIfNull(wwwAuthenticate);
        foreach (var header in wwwAuthenticate)
        {
            if (Parse(header) is { } challenge)
            {
                return challenge;
            }
        }

        return null;
    }

    /// <summary>A <c>Digest …</c> challenge, or <c>null</c> for another scheme, a missing nonce or an unsupported algorithm or qop.</summary>
    public static DigestChallenge? Parse(string? header)
    {
        var text = header?.Trim();
        if (text is null || !text.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parameters = Parameters(text["Digest ".Length..]);
        if (!parameters.TryGetValue("nonce", out var nonce) || nonce.Length == 0)
        {
            return null;
        }

        var algorithm = parameters.GetValueOrDefault("algorithm") is { Length: > 0 } named ? named : "MD5";
        if (Hash(algorithm) is null)
        {
            return null;
        }

        var qopAuth = false;
        if (parameters.TryGetValue("qop", out var qop))
        {
            qopAuth = qop.Split(',', StringSplitOptions.TrimEntries).Contains("auth", StringComparer.OrdinalIgnoreCase);
            if (!qopAuth)
            {
                // Only auth-int offered: it would hash the (empty) body; not needed by any known device.
                return null;
            }
        }

        return new DigestChallenge(parameters.GetValueOrDefault("realm") ?? string.Empty, nonce, parameters.GetValueOrDefault("opaque"), algorithm, qopAuth);
    }

    /// <summary>The <c>Authorization</c> header value for one request.</summary>
    /// <param name="uri">The request target exactly as sent on the request line (path and query).</param>
    /// <param name="cnonce">The client nonce (tests pin it; otherwise random).</param>
    public string Authorize(string method, string uri, string username, string password, string? cnonce = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        ArgumentException.ThrowIfNullOrEmpty(uri);
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);
        var hash = Hash(Algorithm)!;
        var clientNonce = cnonce ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        const string Count = "00000001";
        var ha1 = hash($"{username}:{Realm}:{password}");
        if (Algorithm.EndsWith("-sess", StringComparison.OrdinalIgnoreCase))
        {
            ha1 = hash($"{ha1}:{Nonce}:{clientNonce}");
        }

        var ha2 = hash($"{method}:{uri}");
        var response = QopAuth ? hash($"{ha1}:{Nonce}:{Count}:{clientNonce}:auth:{ha2}") : hash($"{ha1}:{Nonce}:{ha2}");

        var header = new StringBuilder("Digest ");
        header.Append(CultureInfo.InvariantCulture, $"username=\"{Quote(username)}\", realm=\"{Quote(Realm)}\", nonce=\"{Quote(Nonce)}\", uri=\"{Quote(uri)}\", ");
        header.Append(CultureInfo.InvariantCulture, $"algorithm={Algorithm}, response=\"{response}\"");
        if (Opaque is not null)
        {
            header.Append(CultureInfo.InvariantCulture, $", opaque=\"{Quote(Opaque)}\"");
        }

        if (QopAuth)
        {
            header.Append(CultureInfo.InvariantCulture, $", qop=auth, nc={Count}, cnonce=\"{clientNonce}\"");
        }

        return header.ToString();
    }

    private static Func<string, string>? Hash(string algorithm) => algorithm.ToUpperInvariant() switch
    {
        "MD5" or "MD5-SESS" => text => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text))),
        "SHA-256" or "SHA-256-SESS" => text => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))),
        _ => null,
    };

    private static string Quote(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>The <c>name=value</c> / <c>name="quoted value"</c> list of a challenge (names lower-cased; the first wins).</summary>
    private static Dictionary<string, string> Parameters(string text)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && (text[i] == ',' || char.IsWhiteSpace(text[i])))
            {
                i++;
            }

            var nameStart = i;
            while (i < text.Length && text[i] != '=' && text[i] != ',')
            {
                i++;
            }

            var name = text[nameStart..i].Trim();
            if (i >= text.Length || text[i] != '=')
            {
                continue;
            }

            i++;
            while (i < text.Length && text[i] == ' ')
            {
                i++;
            }

            string value;
            if (i < text.Length && text[i] == '"')
            {
                var quoted = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        i++;
                    }

                    quoted.Append(text[i]);
                    i++;
                }

                i++;
                value = quoted.ToString();
            }
            else
            {
                var valueStart = i;
                while (i < text.Length && text[i] != ',')
                {
                    i++;
                }

                value = text[valueStart..i].Trim();
            }

            if (name.Length > 0)
            {
                parameters.TryAdd(name, value);
            }
        }

        return parameters;
    }
}

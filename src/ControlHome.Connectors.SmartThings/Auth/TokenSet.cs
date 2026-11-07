using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ControlHome.Connectors.SmartThings.Auth;

/// <summary>
/// The OAuth tokens of one integration, stored as ONE secret (<see cref="SecretKeys.Tokens"/>) so that a rotation is
/// persisted atomically: SmartThings refresh tokens are single-use, so the new access and refresh tokens must never be
/// saved apart (a crash between two writes would keep a spent refresh token).
/// </summary>
/// <param name="ExpiresAtUtc">When the access token expires (about 24 h after it was issued).</param>
/// <param name="ObtainedAtUtc">When this pair was issued (the weekly keep-alive counts from it).</param>
/// <param name="InstalledAppId">The installed app the token belongs to (for subscriptions later), if SmartThings said.</param>
internal sealed record TokenSet(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset ObtainedAtUtc,
    string? Scope,
    string? InstalledAppId)
{
    public string Serialize() => new JsonObject
    {
        ["accessToken"] = AccessToken,
        ["refreshToken"] = RefreshToken,
        ["expiresAtUtc"] = ExpiresAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        ["obtainedAtUtc"] = ObtainedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        ["scope"] = Scope,
        ["installedAppId"] = InstalledAppId,
    }.ToJsonString();

    /// <summary>The stored tokens, or <c>null</c> when the value is missing or unreadable.</summary>
    public static TokenSet? Deserialize(string? value)
    {
        if (Json.TryParse(value) is not { ValueKind: JsonValueKind.Object } document
            || document.Str("accessToken") is not { Length: > 0 } access
            || document.Str("refreshToken") is not { Length: > 0 } refresh
            || Json.Time(document, "expiresAtUtc") is not { } expires)
        {
            return null;
        }

        return new TokenSet(access, refresh, expires, Json.Time(document, "obtainedAtUtc") ?? expires.AddDays(-1), document.Str("scope"), document.Str("installedAppId"));
    }

    /// <summary>
    /// The secrets to store for this pair: the bundle (the only value the connector reads) plus each token on its own so
    /// the log redactor learns the individual values too.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToSecrets() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [SecretKeys.Tokens] = Serialize(),
        [SecretKeys.AccessToken] = AccessToken,
        [SecretKeys.RefreshToken] = RefreshToken,
    };

    public override string ToString() => $"TokenSet(expires {ExpiresAtUtc:O})";
}

/// <summary>Secret keys of a SmartThings integration.</summary>
internal static class SecretKeys
{
    /// <summary>The OAuth client secret of the household's own app.</summary>
    public const string ClientSecret = "clientSecret";

    /// <summary>A personal access token (development mode).</summary>
    public const string PersonalAccessToken = "personalAccessToken";

    /// <summary>The <see cref="TokenSet"/> bundle — the source of truth for the OAuth tokens.</summary>
    public const string Tokens = "oauthTokens";

    /// <summary>Copies of the current tokens, written after the bundle so the log redactor knows them (never read).</summary>
    public const string AccessToken = "accessToken";

    public const string RefreshToken = "refreshToken";
}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.SmartThings.Api;
using ControlHome.Domain.Devices;

namespace ControlHome.Connectors.SmartThings.Auth;

/// <summary>Where the household's app signs in (<c>…/oauth/authorize</c>) and gets its tokens (<c>…/oauth/token</c>).</summary>
internal sealed record OAuthEndpoints(Uri AuthorizeUrl, Uri TokenUrl);

/// <summary>The app's credentials: the client id and secret the SmartThings CLI printed.</summary>
internal sealed record OAuthApp(string ClientId, string ClientSecret, OAuthEndpoints Endpoints)
{
    public override string ToString() => $"OAuthApp({ClientId})";
}

/// <summary>
/// The SmartThings CLI's sign-in: a public client (no secret, PKCE) whose only allowed redirect is a localhost page.
/// </summary>
internal sealed record CliLogin(string ClientId, Uri AuthorizeUrl, Uri TokenUrl, string RedirectUri, string Scope)
{
    /// <summary>The CLI's sign-in page for this <paramref name="challenge"/> and <paramref name="state"/>.</summary>
    public Uri BuildUrl(string challenge, string state)
    {
        var query = string.Join('&', new (string Key, string Value)[]
        {
            ("response_type", "code"),
            ("client_id", ClientId),
            ("scope", Scope),
            ("redirect_uri", RedirectUri),
            ("code_challenge", challenge),
            ("code_challenge_method", "S256"),
            ("client_type", "USER_LEVEL"),
            ("state", state),
        }.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        return new UriBuilder(AuthorizeUrl) { Query = query }.Uri;
    }
}

/// <summary>PKCE (RFC 7636): a random verifier and its S256 challenge.</summary>
internal static class Pkce
{
    public static string NewVerifier() => Base64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    public static string Challenge(string verifier) => Base64Url(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// The OAuth token endpoint: the authorization-code exchange and the refresh, both with HTTP Basic client
/// authentication (the CLI's sign-in: a public client with PKCE instead). Each call is sent exactly once (never retried: a refresh token is single-use, and a lost answer to
/// a refresh cannot be replayed).
/// </summary>
internal sealed class OAuthClient(HttpClient http, TimeProvider time, TimeSpan timeout)
{
    /// <summary>Access tokens live about 24 h; used when the answer has no <c>expires_in</c>.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(24);

    /// <summary>
    /// The shortest lifetime believed: a token "valid" for seconds would otherwise be refreshed (rotating the single-use
    /// refresh token) on every call. If it really expires sooner, the 401 refreshes it.
    /// </summary>
    public static readonly TimeSpan MinLifetime = TimeSpan.FromMinutes(10);

    /// <summary>The longest lifetime believed (the weekly keep-alive refreshes long before anyway).</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(30);

    public Task<TokenSet> ExchangeCodeAsync(OAuthApp app, string code, string redirectUri, CancellationToken cancellationToken) =>
        PostAsync(
            app,
            [
                new("grant_type", "authorization_code"),
                new("code", code),
                new("redirect_uri", redirectUri),
                new("client_id", app.ClientId),
            ],
            previousRefreshToken: null,
            cancellationToken);

    /// <summary>
    /// The CLI sign-in's code for an access token (used once, to create the household's app; never stored). No client
    /// secret: the PKCE <paramref name="verifier"/> proves this is the sign-in that asked for the code.
    /// </summary>
    public async Task<string> ExchangeCliCodeAsync(CliLogin cli, string code, string verifier, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cli);
        var tokens = await PostAsync(
            cli.TokenUrl,
            basic: null,
            [
                new("grant_type", "authorization_code"),
                new("client_id", cli.ClientId),
                new("code_verifier", verifier),
                new("code", code),
                new("redirect_uri", cli.RedirectUri),
            ],
            previousRefreshToken: string.Empty,
            cancellationToken).ConfigureAwait(false);
        return tokens.AccessToken;
    }

    public Task<TokenSet> RefreshAsync(OAuthApp app, string refreshToken, CancellationToken cancellationToken) =>
        PostAsync(
            app,
            [
                new("grant_type", "refresh_token"),
                new("refresh_token", refreshToken),
                new("client_id", app.ClientId),
            ],
            refreshToken,
            cancellationToken);

    private Task<TokenSet> PostAsync(
        OAuthApp app,
        IEnumerable<KeyValuePair<string, string>> form,
        string? previousRefreshToken,
        CancellationToken cancellationToken) =>
        PostAsync(app.Endpoints.TokenUrl, app, form, previousRefreshToken, cancellationToken);

    /// <param name="basic">The app for HTTP Basic client authentication; <c>null</c> for a public client.</param>
    private async Task<TokenSet> PostAsync(
        Uri tokenUrl,
        OAuthApp? basic,
        IEnumerable<KeyValuePair<string, string>> form,
        string? previousRefreshToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(form),
        };
        if (basic is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{basic.ClientId}:{basic.ClientSecret}")));
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var timeoutSource = new CancellationTokenSource(timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        HttpResponseMessage response;
        string body;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SmartThingsApiException(
                new ConnectorError(ConnectorErrorKind.Transient, IntegrationStatus.Degraded, null),
                null,
                null,
                "The SmartThings token endpoint did not answer in time.");
        }
        catch (HttpRequestException exception)
        {
            throw new SmartThingsApiException(ConnectorErrorClassifier.Classify(exception), null, null, "The SmartThings token endpoint cannot be reached.", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw Failure(response, body);
            }

            var now = time.GetUtcNow();
            if (Json.TryParse(body) is not { ValueKind: JsonValueKind.Object } document
                || document.Str("access_token") is not { Length: > 0 } access)
            {
                throw new SmartThingsApiException(
                    new ConnectorError(ConnectorErrorKind.Permanent, IntegrationStatus.Error, null),
                    response.StatusCode,
                    null,
                    "The SmartThings token endpoint answered without an access token.");
            }

            // SmartThings always rotates the refresh token; keep the previous one only if an answer ever omits it.
            var refresh = document.Str("refresh_token") is { Length: > 0 } rotated ? rotated : previousRefreshToken;
            if (refresh is null)
            {
                throw new SmartThingsApiException(
                    new ConnectorError(ConnectorErrorKind.Permanent, IntegrationStatus.Error, null),
                    response.StatusCode,
                    null,
                    "The SmartThings token endpoint answered without a refresh token.");
            }

            // Clamped before it becomes a TimeSpan: an absurd expires_in must never throw here, after the refresh token
            // was already spent (the new pair would be lost).
            var lifetime = document.Num("expires_in") is { } seconds and > 0
                ? TimeSpan.FromSeconds(Math.Clamp(seconds, MinLifetime.TotalSeconds, MaxLifetime.TotalSeconds))
                : DefaultLifetime;
            return new TokenSet(access, refresh, now + lifetime, now, document.Str("scope"), document.Str("installed_app_id"));
        }
    }

    /// <summary>
    /// OAuth errors: a spent, expired or revoked grant (<c>invalid_grant</c>) and a rejected client mean the user must
    /// re-authorize; plan and rate-limit answers are classified like API answers.
    /// </summary>
    private SmartThingsApiException Failure(HttpResponseMessage response, string body)
    {
        var code = SmartThingsErrors.CodeOf(body);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
            && code is "invalid_grant" or "invalid_client" or "unauthorized_client" or "invalid_token" or null)
        {
            return new SmartThingsApiException(
                new ConnectorError(ConnectorErrorKind.Unauthorized, IntegrationStatus.NeedsReauth, null),
                response.StatusCode,
                code,
                $"SmartThings refused the authorization ({SmartThingsErrors.Sanitize(code ?? ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture))}).");
        }

        return SmartThingsErrors.From(response, body, time, "the token request");
    }
}

using ControlHome.Connectors.Abstractions;
using ControlHome.Connectors.SmartThings.Api;
using ControlHome.Domain.Devices;
using Microsoft.Extensions.Logging;

namespace ControlHome.Connectors.SmartThings.Auth;

/// <summary>Hands out the bearer token for API calls.</summary>
internal interface IAccessTokenSource
{
    /// <summary>Whether a rejected token can be replaced without the user (OAuth; a personal access token cannot).</summary>
    bool CanRefresh { get; }

    /// <summary>A token to use now (refreshing first when it is due).</summary>
    /// <exception cref="SmartThingsApiException">The token could not be refreshed.</exception>
    ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// SmartThings rejected <paramref name="rejectedToken"/> (401): a fresh token, or <c>null</c> when there is none. If
    /// another caller already replaced the rejected token, that one is returned without a second refresh.
    /// </summary>
    ValueTask<string?> RefreshAfterRejectionAsync(string rejectedToken, CancellationToken cancellationToken);
}

/// <summary>A personal access token (development mode): valid 24 h after it was created, never refreshed.</summary>
internal sealed class PersonalAccessToken(string token) : IAccessTokenSource
{
    public bool CanRefresh => false;

    public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken) => ValueTask.FromResult(token);

    public ValueTask<string?> RefreshAfterRejectionAsync(string rejectedToken, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);

    public override string ToString() => "PersonalAccessToken";
}

/// <summary>
/// The OAuth tokens of one integration. Refreshes when less than <see cref="RefreshBefore"/> of the access token is left
/// (or less than half of its lifetime, for tokens shorter than twice that margin: never on every call) and at least every
/// <see cref="KeepAlive"/> (an unused refresh token dies after about 30 days). Refreshes are single-flight (one at a
/// time, re-checked after waiting, so concurrent callers share one refresh) and the rotated pair is persisted as one
/// secret before anyone uses it. A refresh is never cancelled half-way by a caller: once the request is sent its answer
/// is read and saved (a lost answer would spend the single-use refresh token for nothing). None is started once the
/// integration is stopping (<c>stopping</c>): the host then refuses secret writes. Every rotation is also recorded in the
/// factory's <see cref="TokenLineage"/>, so a pair that could not be saved (a refresh in flight while the run is stopped,
/// a database outage) is carried on by the integration's next run instead of being lost with this one.
/// </summary>
internal sealed partial class TokenManager : IAccessTokenSource
{
    private readonly OAuthClient _client;
    private readonly OAuthApp _app;
    private readonly ISecretStore _secrets;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationToken _stopping;
    private readonly TokenLineage? _lineage;
    private readonly Guid _integrationId;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TokenSet _current;
    private bool _unsaved;
    private int _refreshes;

    /// <param name="stopping">The run's stop: once it is requested no refresh is started.</param>
    /// <param name="lineage">Where the integration's rotations are remembered for its next run, or <c>null</c>.</param>
    /// <param name="integrationId">The integration (the key in <paramref name="lineage"/>).</param>
    /// <param name="unsaved">
    /// <paramref name="tokens"/> is newer than the stored pair (taken over from a run that could not save it): it is
    /// saved before anything else (<see cref="SaveUnsavedAsync"/>, or the next call).
    /// </param>
    public TokenManager(
        OAuthClient client,
        OAuthApp app,
        TokenSet tokens,
        ISecretStore secrets,
        TimeProvider time,
        ILogger logger,
        TimeSpan refreshBefore,
        TimeSpan keepAlive,
        CancellationToken stopping,
        TokenLineage? lineage = null,
        Guid integrationId = default,
        bool unsaved = false)
    {
        _client = client;
        _app = app;
        _current = tokens;
        _secrets = secrets;
        _time = time;
        _logger = logger;
        _stopping = stopping;
        _lineage = lineage;
        _integrationId = integrationId;
        _unsaved = unsaved;
        RefreshBefore = refreshBefore;
        KeepAlive = keepAlive;
    }

    public TimeSpan RefreshBefore { get; }

    public TimeSpan KeepAlive { get; }

    public bool CanRefresh => true;

    /// <summary>Refreshes done so far (tests: two concurrent callers → exactly one).</summary>
    public int Refreshes => Volatile.Read(ref _refreshes);

    /// <summary>The current pair (tests, diagnostics).</summary>
    public TokenSet Current => Volatile.Read(ref _current);

    /// <summary>Whether a rotated pair could not be saved yet (the next call tries again before anything else).</summary>
    public bool HasUnsavedTokens => Volatile.Read(ref _unsaved);

    public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var current = Current;
        if (!IsDue(current) && !HasUnsavedTokens)
        {
            return current.AccessToken;
        }

        return (await RefreshAsync(rejectedToken: null, cancellationToken).ConfigureAwait(false)).AccessToken;
    }

    public async ValueTask<string?> RefreshAfterRejectionAsync(string rejectedToken, CancellationToken cancellationToken) =>
        (await RefreshAsync(rejectedToken, cancellationToken).ConfigureAwait(false)).AccessToken;

    /// <summary>Whether the pair should be refreshed now.</summary>
    public bool IsDue(TokenSet tokens)
    {
        var now = _time.GetUtcNow();
        var lifetime = tokens.ExpiresAtUtc - tokens.ObtainedAtUtc;
        var margin = lifetime > TimeSpan.Zero && lifetime / 2 < RefreshBefore ? lifetime / 2 : RefreshBefore;
        return tokens.ExpiresAtUtc - now < margin || now - tokens.ObtainedAtUtc >= KeepAlive;
    }

    /// <param name="rejectedToken">A token SmartThings rejected (refresh unless it was replaced meanwhile), or <c>null</c> to refresh only when due.</param>
    private async Task<TokenSet> RefreshAsync(string? rejectedToken, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A rotated pair that could not be saved comes first.
            if (_unsaved)
            {
                await TrySaveAsync(_current).ConfigureAwait(false);
            }

            var current = _current;
            var needed = rejectedToken is null ? IsDue(current) : string.Equals(rejectedToken, current.AccessToken, StringComparison.Ordinal);
            if (!needed)
            {
                // Someone else refreshed while this caller waited.
                return current;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (_stopping.IsCancellationRequested)
            {
                // The rotated pair could not be saved any more: keep the current one for the next run.
                throw new SmartThingsApiException(
                    new ConnectorError(ConnectorErrorKind.Transient, IntegrationStatus.Degraded, null),
                    null,
                    null,
                    "The SmartThings integration is stopping; its token is refreshed by the next run.");
            }

            LogRefreshing(_logger, current.ExpiresAtUtc);

            // Not the caller's token: once sent, the answer must be read and saved even if the caller gives up.
            var next = await _client.RefreshAsync(_app, current.RefreshToken, CancellationToken.None).ConfigureAwait(false);
            Interlocked.Increment(ref _refreshes);
            if (!string.Equals(next.RefreshToken, current.RefreshToken, StringComparison.Ordinal))
            {
                // Before the save, which may fail: the next run must be able to carry the new pair on.
                _lineage?.Rotated(_integrationId, current.RefreshToken, next);
            }

            Volatile.Write(ref _current, next);
            Volatile.Write(ref _unsaved, true);
            if (await TrySaveAsync(next).ConfigureAwait(false))
            {
                LogRefreshed(_logger, next.ExpiresAtUtc);
            }

            // Even unsaved, the new pair is the only valid one: use it (and keep trying to save it).
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Saves the current pair now if it is newer than the stored one; <c>false</c> when that failed (retried later).</summary>
    public async Task<bool> SaveUnsavedAsync()
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            return !_unsaved || await TrySaveAsync(_current).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Persists the pair (one write: the bundle, the source of truth), then the copies the log redactor learns from.
    /// <c>false</c> when the bundle could not be saved: the pair stays in memory and the next call tries again.
    /// </summary>
    private async Task<bool> TrySaveAsync(TokenSet tokens)
    {
        try
        {
            await _secrets.SetAsync(SecretKeys.Tokens, tokens.Serialize(), CancellationToken.None).ConfigureAwait(false);
            Volatile.Write(ref _unsaved, false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogSaveFailed(_logger, exception.GetType().Name);
            return false;
        }

        foreach (var key in (string[])[SecretKeys.AccessToken, SecretKeys.RefreshToken])
        {
            try
            {
                await _secrets.SetAsync(key, key == SecretKeys.AccessToken ? tokens.AccessToken : tokens.RefreshToken, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Only the redaction copies: the bundle is saved.
                LogSaveFailed(_logger, exception.GetType().Name);
            }
        }

        return true;
    }

    public override string ToString() => $"TokenManager({_app.ClientId})";

    [LoggerMessage(Level = LogLevel.Information, Message = "SmartThings: refreshing the access token (expires {ExpiresAtUtc:O})")]
    private static partial void LogRefreshing(ILogger logger, DateTimeOffset expiresAtUtc);

    [LoggerMessage(Level = LogLevel.Information, Message = "SmartThings: access token refreshed and saved (valid until {ExpiresAtUtc:O})")]
    private static partial void LogRefreshed(ILogger logger, DateTimeOffset expiresAtUtc);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SmartThings: could not save the rotated tokens ({ExceptionType})")]
    private static partial void LogSaveFailed(ILogger logger, string exceptionType);
}

using System.Collections.Concurrent;
using ControlHome.Api.Hosting;
using ControlHome.Application.Auth;
using ControlHome.Application.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Api.Realtime;

/// <summary>
/// The open <see cref="HomeHub"/> connections by user, with the security stamp each was opened with. A WebSocket is
/// authenticated once, when it opens, and the session cookie's stamp is re-validated only on HTTP requests; this registry is
/// how a user's live connections are closed when their access changes (<see cref="IUserSessionRevoker"/>: disabled,
/// deleted, role, password) and how <see cref="HubSessionValidator"/> re-checks them.
/// </summary>
public sealed partial class HubConnectionRegistry(ILogger<HubConnectionRegistry> logger) : IUserSessionRevoker
{
    private readonly ConcurrentDictionary<string, OpenHubConnection> _connections = new(StringComparer.Ordinal);

    /// <summary>Open connections (all users).</summary>
    public int Count => _connections.Count;

    public void Add(HubCallerContext context, Guid userId, string? securityStamp)
    {
        ArgumentNullException.ThrowIfNull(context);
        _connections[context.ConnectionId] = new OpenHubConnection(context, userId, securityStamp);
    }

    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    /// <summary>The open connections of <paramref name="userId"/>.</summary>
    public int CountFor(Guid userId) => _connections.Values.Count(c => c.UserId == userId);

    public IReadOnlyList<OpenHubConnection> Snapshot() => [.. _connections.Values];

    /// <summary>Closes one connection (it is forgotten now; <see cref="HomeHub.OnDisconnectedAsync"/> follows).</summary>
    public void Abort(OpenHubConnection connection, string reason)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (_connections.TryRemove(new KeyValuePair<string, OpenHubConnection>(connection.Context.ConnectionId, connection)))
        {
            LogAborted(connection.UserId, reason);
            connection.Context.Abort();
        }
    }

    public Task RevokeAsync(Guid userId, CancellationToken cancellationToken)
    {
        foreach (var connection in _connections.Values.Where(c => c.UserId == userId).ToList())
        {
            Abort(connection, "access changed");
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Closed a live-update connection of user {UserId}: {Reason}")]
    private partial void LogAborted(Guid userId, string reason);
}

/// <summary>An open hub connection: its user and the security stamp of the session it was opened with.</summary>
public sealed record OpenHubConnection(HubCallerContext Context, Guid UserId, string? SecurityStamp);

/// <summary>
/// Backstop for <see cref="IUserSessionRevoker"/>: every <see cref="IdentitySetup.SecurityStampValidationInterval"/> (the
/// cookie's own re-check interval) the open hub connections are checked against the user rows, and those whose user was
/// deleted or disabled or whose security stamp changed (by any path) are closed.
/// </summary>
public sealed partial class HubSessionValidator(
    HubConnectionRegistry connections,
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    ILogger<HubSessionValidator> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(IdentitySetup.SecurityStampValidationInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await ValidateAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // The database may be unavailable: try again at the next tick.
                    LogValidationFailed(ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>Closes every open connection whose user no longer has the session it was opened with.</summary>
    public async Task ValidateAsync(CancellationToken cancellationToken)
    {
        var open = connections.Snapshot();
        if (open.Count == 0)
        {
            return;
        }

        var ids = open.Select(c => c.UserId).Distinct().ToList();
        Dictionary<Guid, (string? Stamp, bool Disabled)> users;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            users = await userManager.Users.AsNoTracking()
                .Where(u => ids.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => (u.SecurityStamp, u.IsDisabled), cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var connection in open)
        {
            if (!users.TryGetValue(connection.UserId, out var user)
                || user.Disabled
                || !string.Equals(user.Stamp, connection.SecurityStamp, StringComparison.Ordinal))
            {
                connections.Abort(connection, "session no longer valid");
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Re-checking live-update connections failed ({ExceptionType})")]
    private partial void LogValidationFailed(string exceptionType);
}

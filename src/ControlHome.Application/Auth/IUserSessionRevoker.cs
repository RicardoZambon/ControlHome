namespace ControlHome.Application.Auth;

/// <summary>
/// Ends a user's live connections (the Api closes their <c>/hubs/home</c> connections) when their access changed: disabled,
/// deleted, role changed, password changed or reset (their security stamp changed). HTTP requests need no help: the
/// cookie's security stamp is re-validated on them. Called after the change is committed; never throws.
/// </summary>
public interface IUserSessionRevoker
{
    Task RevokeAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>For hosts without live connections.</summary>
public sealed class NoUserSessions : IUserSessionRevoker
{
    public static NoUserSessions Instance { get; } = new();

    public Task RevokeAsync(Guid userId, CancellationToken cancellationToken) => Task.CompletedTask;
}

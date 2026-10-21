using System.Security.Claims;
using ControlHome.Application.Identity;
using ControlHome.Application.Realtime;
using ControlHome.Application.Runtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Api.Realtime;

/// <summary>
/// The live-update hub at <see cref="HomeHubMessages.Path"/> for signed-in users (session cookie). Server → client only:
/// it has no methods clients could call. Messages: see <see cref="HomeHubMessages"/> and <see cref="IHomeClient"/>.
/// The WebSocket handshake passes the request edge's same-origin check like any mutation; the negotiate POST also needs
/// <c>X-ControlHome: 1</c> (the SPA's hub connection sets it).
/// <para>
/// Revocation: every connection is registered by user (<see cref="HubConnectionRegistry"/>) and its session re-validated
/// against the user row when it opens (the cookie's stamp is only re-checked every minute); it is closed when the user is
/// disabled, deleted, demoted or their password changes (<see cref="Application.Auth.IUserSessionRevoker"/>), and
/// <see cref="HubSessionValidator"/> re-checks open connections every minute as a backstop.
/// </para>
/// </summary>
[Authorize]
public sealed class HomeHub(HubConnectionRegistry connections, UserManager<AppUser> userManager) : Hub<IHomeClient>
{
    public override async Task OnConnectedAsync()
    {
        var principal = Context.User ?? new ClaimsPrincipal();
        if (!Guid.TryParse(userManager.GetUserId(principal), out var userId))
        {
            Context.Abort();
            return;
        }

        // Registered first, so a revocation from now on reaches this connection; then checked, so one that happened just
        // before (the cookie is re-validated only every minute) closes it too.
        var stamp = principal.FindFirstValue(userManager.Options.ClaimsIdentity.SecurityStampClaimType);
        connections.Add(Context, userId, stamp);
        var user = await userManager.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.SecurityStamp, u.IsDisabled })
            .SingleOrDefaultAsync(Context.ConnectionAborted);
        if (user is null || user.IsDisabled || !string.Equals(user.SecurityStamp, stamp, StringComparison.Ordinal))
        {
            connections.Remove(Context.ConnectionId);
            Context.Abort();
            return;
        }

        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>The client methods of <see cref="HomeHub"/> (names match <see cref="HomeHubMessages"/>).</summary>
public interface IHomeClient
{
    Task StateChanged(IReadOnlyList<StateDelta> deltas);

    Task AvailabilityChanged(AvailabilityChangedDto change);

    Task DevicesChanged();

    Task IntegrationStatusChanged(IntegrationStatusChangedDto change);

    Task CommandStatus(CommandStatusDto status);

    Task SystemStatusChanged(SystemStatusDto status);
}

/// <summary><see cref="IRealtimePublisher"/> over <see cref="HomeHub"/>: every message goes to every connected user.</summary>
public sealed class HomeHubPublisher(IHubContext<HomeHub, IHomeClient> hub) : IRealtimePublisher
{
    public Task StateChangedAsync(IReadOnlyList<StateDelta> deltas, CancellationToken cancellationToken) =>
        deltas.Count == 0 ? Task.CompletedTask : hub.Clients.All.StateChanged(deltas);

    public Task AvailabilityChangedAsync(AvailabilityChangedDto change, CancellationToken cancellationToken) =>
        hub.Clients.All.AvailabilityChanged(change);

    public Task DevicesChangedAsync(CancellationToken cancellationToken) => hub.Clients.All.DevicesChanged();

    public Task IntegrationStatusChangedAsync(IntegrationStatusChangedDto change, CancellationToken cancellationToken) =>
        hub.Clients.All.IntegrationStatusChanged(change);

    public Task CommandStatusAsync(CommandStatusDto status, CancellationToken cancellationToken) => hub.Clients.All.CommandStatus(status);

    public Task SystemStatusChangedAsync(SystemStatusDto status, CancellationToken cancellationToken) =>
        hub.Clients.All.SystemStatusChanged(status);
}

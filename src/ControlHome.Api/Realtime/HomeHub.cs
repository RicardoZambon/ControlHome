using ControlHome.Application.Realtime;
using ControlHome.Application.Runtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ControlHome.Api.Realtime;

/// <summary>
/// The live-update hub at <see cref="HomeHubMessages.Path"/> for signed-in users (session cookie). Server → client only:
/// it has no methods clients could call. Messages: see <see cref="HomeHubMessages"/> and <see cref="IHomeClient"/>.
/// The WebSocket handshake passes the request edge's same-origin check like any mutation; the negotiate POST also needs
/// <c>X-ControlHome: 1</c> (the SPA's hub connection sets it).
/// </summary>
[Authorize]
public sealed class HomeHub : Hub<IHomeClient>;

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

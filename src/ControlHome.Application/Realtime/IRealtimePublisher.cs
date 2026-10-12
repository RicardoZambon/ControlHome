using ControlHome.Application.Runtime;

namespace ControlHome.Application.Realtime;

/// <summary>
/// Pushes live updates to connected clients (the Api implements it with the SignalR hub <c>/hubs/home</c>; see
/// <see cref="HomeHubMessages"/>). Calls never throw for a disconnected client and should not block for long.
/// </summary>
public interface IRealtimePublisher
{
    Task StateChangedAsync(IReadOnlyList<StateDelta> deltas, CancellationToken cancellationToken);

    Task AvailabilityChangedAsync(AvailabilityChangedDto change, CancellationToken cancellationToken);

    Task DevicesChangedAsync(CancellationToken cancellationToken);

    Task IntegrationStatusChangedAsync(IntegrationStatusChangedDto change, CancellationToken cancellationToken);

    Task CommandStatusAsync(CommandStatusDto status, CancellationToken cancellationToken);

    Task SystemStatusChangedAsync(SystemStatusDto status, CancellationToken cancellationToken);
}

/// <summary>Publishes nothing (hosts without a hub: tools, tests).</summary>
public sealed class NullRealtimePublisher : IRealtimePublisher
{
    public static NullRealtimePublisher Instance { get; } = new();

    public Task StateChangedAsync(IReadOnlyList<StateDelta> deltas, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task AvailabilityChangedAsync(AvailabilityChangedDto change, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DevicesChangedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task IntegrationStatusChangedAsync(IntegrationStatusChangedDto change, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CommandStatusAsync(CommandStatusDto status, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SystemStatusChangedAsync(SystemStatusDto status, CancellationToken cancellationToken) => Task.CompletedTask;
}

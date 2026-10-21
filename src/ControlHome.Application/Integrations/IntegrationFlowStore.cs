using System.Collections.Concurrent;
using ControlHome.Connectors.Abstractions;

namespace ControlHome.Application.Integrations;

/// <summary>A flow in progress: the connector's step machine and where it stands. Only its owner may continue it.</summary>
public sealed class FlowSession(Guid id, string connectorKey, Guid? integrationId, Guid ownerId, IConnectorFlow flow)
{
    public Guid Id { get; } = id;

    public string ConnectorKey { get; } = connectorKey;

    /// <summary>The integration being re-authorized or reconfigured; <c>null</c> during setup.</summary>
    public Guid? IntegrationId { get; } = integrationId;

    /// <summary>The user who started it.</summary>
    public Guid OwnerId { get; } = ownerId;

    public IConnectorFlow Flow { get; } = flow;

    /// <summary>The step waiting for an answer.</summary>
    public string? CurrentStepId { get; set; }

    /// <summary>The secret fields of the step waiting for an answer (their values are registered with the log redactor).</summary>
    public IReadOnlyList<string> SecretFields { get; set; } = [];

    public DateTimeOffset ExpiresAtUtc { get; set; }

    /// <summary>One answer at a time (double clicks, two tabs).</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);
}

/// <summary>
/// The flows in progress, in memory (a restart forgets them): each expires <see cref="Lifetime"/> after its last step, a
/// user has at most <see cref="MaxFlowsPerUser"/> at once (the oldest is dropped), and finished, cancelled or expired
/// flows are disposed.
/// </summary>
public sealed class IntegrationFlowStore(TimeProvider timeProvider)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    public const int MaxFlowsPerUser = 10;

    private readonly ConcurrentDictionary<Guid, FlowSession> _flows = new();

    public int Count => _flows.Count;

    /// <summary>Keeps <paramref name="session"/> (dropping expired flows and the owner's oldest beyond the limit).</summary>
    public async Task AddAsync(FlowSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        await SweepAsync().ConfigureAwait(false);
        session.ExpiresAtUtc = timeProvider.GetUtcNow() + Lifetime;
        _flows[session.Id] = session;

        var owned = _flows.Values.Where(f => f.OwnerId == session.OwnerId).OrderBy(f => f.ExpiresAtUtc).ToList();
        foreach (var oldest in owned.Take(Math.Max(0, owned.Count - MaxFlowsPerUser)))
        {
            await RemoveAsync(oldest.Id).ConfigureAwait(false);
        }
    }

    /// <summary>The owner's flow, or <c>null</c> when it is unknown, expired or someone else's.</summary>
    public FlowSession? Find(Guid id, Guid ownerId) =>
        _flows.TryGetValue(id, out var session) && session.OwnerId == ownerId && session.ExpiresAtUtc > timeProvider.GetUtcNow()
            ? session
            : null;

    /// <summary>Another <see cref="Lifetime"/> after a step.</summary>
    public void Touch(FlowSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.ExpiresAtUtc = timeProvider.GetUtcNow() + Lifetime;
    }

    /// <summary>Forgets and disposes a flow (finished, cancelled, expired).</summary>
    public async Task RemoveAsync(Guid id)
    {
        if (_flows.TryRemove(id, out var session))
        {
            await DisposeQuietlyAsync(session).ConfigureAwait(false);
        }
    }

    /// <summary>Disposes every flow (shutdown).</summary>
    public async Task ClearAsync()
    {
        foreach (var id in _flows.Keys.ToList())
        {
            await RemoveAsync(id).ConfigureAwait(false);
        }
    }

    private async Task SweepAsync()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var expired in _flows.Values.Where(f => f.ExpiresAtUtc <= now).Select(f => f.Id).ToList())
        {
            await RemoveAsync(expired).ConfigureAwait(false);
        }
    }

    private static async Task DisposeQuietlyAsync(FlowSession session)
    {
        try
        {
            await session.Flow.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A connector's flow failing to clean up (e.g. a broker connection) must not fail the request.
        }
    }
}

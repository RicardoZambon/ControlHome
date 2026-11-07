using ControlHome.Connectors.SmartThings.Mapping;
using ControlHome.Connectors.SmartThings.Model;

namespace ControlHome.Connectors.SmartThings.Runtime;

/// <summary>
/// Capability definitions (<c>GET /v1/capabilities/{id}/{version}</c>) by id and version, kept in memory for
/// <see cref="SmartThingsOptions.DefinitionLifetime"/> (they hardly ever change) and shared by every integration of the
/// process. One that cannot be fetched is <see cref="DefinitionState.Unavailable"/> and asked for again after
/// <see cref="SmartThingsOptions.DefinitionRetry"/>. Thread-safe.
/// </summary>
internal sealed class CapabilityDefinitionCache(SmartThingsOptions options)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(string Id, int Version), Entry> _entries = [];

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>The definition as known now. An expired one is still served (and refreshed by the next fetch round).</summary>
    public DefinitionLookup Lookup(CapabilityRef capability)
    {
        lock (_lock)
        {
            return _entries.TryGetValue((capability.Id, capability.Version), out var entry)
                ? entry.Document is { } document ? DefinitionLookup.Of(document) : DefinitionLookup.Unavailable
                : DefinitionLookup.Pending;
        }
    }

    /// <summary>Whether the capability should be fetched now (never fetched, expired, or a failure old enough to retry).</summary>
    public bool IsDue(CapabilityRef capability, DateTimeOffset nowUtc)
    {
        lock (_lock)
        {
            return !_entries.TryGetValue((capability.Id, capability.Version), out var entry)
                || nowUtc - entry.FetchedAtUtc >= (entry.Document is null ? options.DefinitionRetry : options.DefinitionLifetime);
        }
    }

    /// <summary>Records a fetch: the definition, or <c>null</c> when SmartThings has none for this account.</summary>
    public void Store(CapabilityRef capability, CapabilityDocument? document, DateTimeOffset nowUtc)
    {
        lock (_lock)
        {
            // A refetch that failed does not replace a definition that is known.
            if (document is null && _entries.TryGetValue((capability.Id, capability.Version), out var known) && known.Document is not null)
            {
                _entries[(capability.Id, capability.Version)] = known with { FetchedAtUtc = nowUtc - options.DefinitionLifetime + options.DefinitionRetry };
                return;
            }

            _entries[(capability.Id, capability.Version)] = new Entry(document, nowUtc);
        }
    }

    private sealed record Entry(CapabilityDocument? Document, DateTimeOffset FetchedAtUtc);
}

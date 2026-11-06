using ControlHome.Application.Abstractions;
using ControlHome.Domain.Capabilities;
using ControlHome.Domain.Devices;
using ControlHome.Domain.History;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Infrastructure.History;

/// <summary>An attribute the rollups know: its endpoint, capability, definition and statistics kind.</summary>
internal sealed record HistoryAttribute(int AttributeId, Guid EndpointId, string CapabilityId, AttributeDefinition Definition, StatisticKind Kind);

/// <summary>
/// The attributes the rollups work on, read from the inventory: those of non-retired capabilities on devices that are not
/// orphaned (an orphaned or retired one never reports again, so its last value would be carried forward forever).
/// </summary>
internal sealed class HistoryCatalog
{
    private HistoryCatalog(IReadOnlyList<HistoryAttribute> attributes) => Attributes = attributes;

    public IReadOnlyList<HistoryAttribute> Attributes { get; }

    /// <summary>Attributes rolled up into statistics (measurements and counters).</summary>
    public IEnumerable<HistoryAttribute> WithStatistics => Attributes.Where(a => a.Kind != StatisticKind.None);

    public static Task<HistoryCatalog> LoadAsync(IControlHomeDb db, CancellationToken cancellationToken) => LoadAsync(db, null, cancellationToken);

    /// <summary>The catalog, restricted to the attributes of <paramref name="endpointIds"/> when given.</summary>
    public static async Task<HistoryCatalog> LoadAsync(IControlHomeDb db, IReadOnlyCollection<Guid>? endpointIds, CancellationToken cancellationToken)
    {
        var query =
            from attribute in db.DeviceAttributes
            join capability in db.EndpointCapabilities on attribute.EndpointCapabilityId equals capability.Id
            join endpoint in db.Endpoints on capability.EndpointId equals endpoint.Id
            join device in db.Devices on endpoint.DeviceId equals device.Id
            where !capability.Retired && !device.IsOrphaned
            select new { attribute.Id, attribute.Key, capability.CapabilityId, capability.GenericDefinitionJson, capability.EndpointId };
        if (endpointIds is not null)
        {
            var ids = endpointIds.ToList();
            query = query.Where(row => ids.Contains(row.EndpointId));
        }

        var rows = await query
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var definitions = new Dictionary<(string, string?), CapabilityDefinition?>();
        var attributes = new List<HistoryAttribute>();
        foreach (var row in rows)
        {
            var key = (row.CapabilityId, row.GenericDefinitionJson);
            if (!definitions.TryGetValue(key, out var capability))
            {
                capability = EndpointCapability.ResolveDefinition(row.CapabilityId, row.GenericDefinitionJson);
                definitions[key] = capability;
            }

            if (capability?.FindAttribute(row.Key) is { } definition)
            {
                attributes.Add(new HistoryAttribute(row.Id, row.EndpointId, row.CapabilityId, definition, HistoryClassification.KindOf(definition)));
            }
        }

        return new HistoryCatalog(attributes);
    }

    /// <summary>
    /// Brings <c>EnergySourceSelection</c> in line with the inventory: each endpoint gets the source
    /// <see cref="HistoryClassification.PickEnergySource"/> picks (a user's choice is never changed); an automatic choice
    /// whose endpoint no longer has a source is removed. Returns every selection.
    /// </summary>
    public async Task<IReadOnlyList<EnergySourceSelection>> SyncEnergySourcesAsync(
        IControlHomeDb db,
        Func<Guid, bool> hasVendorWindows,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await db.EnergySourceSelections.ToDictionaryAsync(s => s.EndpointId, cancellationToken).ConfigureAwait(false);
        var picks = Attributes
            .GroupBy(a => a.EndpointId)
            .Select(g => (EndpointId: g.Key, Pick: HistoryClassification.PickEnergySource(
                [.. g.Select(a => new EnergyCandidate(a.AttributeId, a.CapabilityId, a.Definition))],
                hasVendorWindows(g.Key))))
            .Where(p => p.Pick is not null)
            .ToDictionary(p => p.EndpointId, p => p.Pick!.Value);

        foreach (var (endpointId, (source, attributeId)) in picks)
        {
            if (!existing.TryGetValue(endpointId, out var selection))
            {
                selection = new EnergySourceSelection { EndpointId = endpointId };
                db.EnergySourceSelections.Add(selection);
                existing[endpointId] = selection;
            }
            else if (selection.IsUserOverride || (selection.Source == source && selection.AttributeId == attributeId))
            {
                continue;
            }

            selection.Source = source;
            selection.AttributeId = attributeId;
            selection.UpdatedAtUtc = now;
        }

        foreach (var stale in existing.Values.Where(s => !s.IsUserOverride && !picks.ContainsKey(s.EndpointId)).ToList())
        {
            db.EnergySourceSelections.Remove(stale);
            existing.Remove(stale.EndpointId);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return [.. existing.Values];
    }
}

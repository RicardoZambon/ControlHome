using ControlHome.Application.Abstractions;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.History;
using ControlHome.Infrastructure.Devices;
using ControlHome.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlHome.Infrastructure.History;

/// <summary>What one energy-window report did.</summary>
/// <param name="Endpoints">Endpoints whose hours were written.</param>
/// <param name="Hours">Rows upserted into <c>EnergyHourly</c>.</param>
/// <param name="SkippedEndpoints">Endpoints whose energy comes from another source (a counter, a user's choice).</param>
/// <param name="IgnoredWindows">Invalid windows, windows in the future, or windows of unknown or orphaned devices/endpoints.</param>
public sealed record EnergyWindowWriteResult(int Endpoints, int Hours, int SkippedEndpoints, int IgnoredWindows);

/// <summary>
/// Stores vendor energy windows (<see cref="IConnectorContext.ReportEnergyWindowsAsync"/>) as <c>EnergyHourly</c> rows with
/// <see cref="EnergySource.VendorWindow"/>, spread by <see cref="EnergyWindowMath.Spread"/> (windows longer than an hour
/// are <c>Estimated</c>: not hourly data). Per endpoint, only the hours the report touches are upserted, so reporting the
/// same windows again writes the same rows. An endpoint takes vendor windows only when they are its energy source: no
/// energy counter (which always wins, <see cref="HistoryClassification.PickEnergySource"/>) and no user choice of another
/// source; the writer then selects <see cref="EnergySource.VendorWindow"/> for it (replacing an automatic integrated-power
/// choice), and the rollup leaves its energy alone. Runs exclusively with the rollups (<see cref="HistoryRollup"/>), which
/// also write <c>EnergyHourly</c> and the source selections.
/// </summary>
public sealed partial class VendorEnergyWriter(
    IServiceScopeFactory scopes,
    IDatabaseConnectionProvider connections,
    RuntimeClock clock,
    HistoryRollup rollup,
    IOptions<HistoryOptions> options,
    ILogger<VendorEnergyWriter> logger) : IEnergyWindowSink
{
    /// <summary>The most windows one report may carry (two months of daily data for a few hundred endpoints).</summary>
    public const int MaxWindowsPerReport = 20_000;

    /// <summary>Clock skew tolerated for a window's end (a vendor's "now" may be a little ahead of ours).</summary>
    private static readonly TimeSpan _futureTolerance = TimeSpan.FromMinutes(5);

    async Task IEnergyWindowSink.WriteAsync(Guid integrationId, IReadOnlyList<EnergyWindowReport> windows, CancellationToken cancellationToken) =>
        await WriteAsync(integrationId, windows, cancellationToken).ConfigureAwait(false);

    /// <exception cref="ArgumentException">More than <see cref="MaxWindowsPerReport"/> windows.</exception>
    public Task<EnergyWindowWriteResult> WriteAsync(Guid integrationId, IReadOnlyList<EnergyWindowReport> windows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (windows.Count > MaxWindowsPerReport)
        {
            throw new ArgumentException($"At most {MaxWindowsPerReport} energy windows can be reported at once.", nameof(windows));
        }

        return rollup.RunExclusiveAsync(() => WriteCoreAsync(integrationId, windows, cancellationToken), cancellationToken);
    }

    private async Task<EnergyWindowWriteResult> WriteCoreAsync(Guid integrationId, IReadOnlyList<EnergyWindowReport> windows, CancellationToken cancellationToken)
    {
        var now = clock.Time.GetUtcNow();
        var ignored = 0;
        var valid = new List<EnergyWindowReport>(windows.Count);
        foreach (var window in windows)
        {
            if (window is null
                || string.IsNullOrEmpty(window.ExternalDeviceId)
                || string.IsNullOrEmpty(window.EndpointKey)
                || EnergyWindowMath.Validate(new EnergyWindow(window.StartUtc, window.EndUtc, window.Wh)) is not null
                || window.EndUtc > now + _futureTolerance)
            {
                ignored++;
                continue;
            }

            valid.Add(window);
        }

        if (valid.Count == 0)
        {
            return Done(integrationId, new EnergyWindowWriteResult(0, 0, 0, ignored));
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IControlHomeDb>();

        // External ids match case-insensitively, like the inventory's unique key.
        var externalIds = valid.Select(w => w.ExternalDeviceId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var endpoints = await (
                from endpoint in db.Endpoints
                join device in db.Devices on endpoint.DeviceId equals device.Id
                where device.IntegrationId == integrationId && !device.IsOrphaned && externalIds.Contains(device.ExternalId)
                select new { endpoint.Id, endpoint.Key, device.ExternalId })
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var byAddress = endpoints
            .GroupBy(e => Address(e.ExternalId, e.Key))
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);

        var byEndpoint = new Dictionary<Guid, List<EnergyWindow>>();
        foreach (var window in valid)
        {
            if (!byAddress.TryGetValue(Address(window.ExternalDeviceId, window.EndpointKey), out var endpointId))
            {
                ignored++;
                continue;
            }

            if (!byEndpoint.TryGetValue(endpointId, out var list))
            {
                list = [];
                byEndpoint[endpointId] = list;
            }

            list.Add(new EnergyWindow(window.StartUtc, window.EndUtc, window.Wh));
        }

        if (byEndpoint.Count == 0)
        {
            return Done(integrationId, new EnergyWindowWriteResult(0, 0, 0, ignored));
        }

        var endpointIds = byEndpoint.Keys.ToList();
        var catalog = await HistoryCatalog.LoadAsync(db, endpointIds, cancellationToken).ConfigureAwait(false);
        var candidates = catalog.Attributes.ToLookup(a => a.EndpointId);
        var selections = await db.EnergySourceSelections
            .Where(s => endpointIds.Contains(s.EndpointId))
            .ToDictionaryAsync(s => s.EndpointId, cancellationToken)
            .ConfigureAwait(false);

        var skipped = 0;
        var accepted = new List<Guid>();
        foreach (var endpointId in endpointIds)
        {
            selections.TryGetValue(endpointId, out var selection);
            if (selection is { IsUserOverride: true })
            {
                if (selection.Source != EnergySource.VendorWindow)
                {
                    skipped++;
                    continue;
                }
            }
            else
            {
                var pick = HistoryClassification.PickEnergySource(
                    [.. candidates[endpointId].Select(a => new EnergyCandidate(a.AttributeId, a.CapabilityId, a.Definition))],
                    hasVendorWindows: true);
                if (pick is not { Source: EnergySource.VendorWindow })
                {
                    // An energy counter: it is the endpoint's source, the windows would count its energy twice.
                    skipped++;
                    continue;
                }

                if (selection is null)
                {
                    db.EnergySourceSelections.Add(new EnergySourceSelection
                    {
                        EndpointId = endpointId,
                        Source = EnergySource.VendorWindow,
                        UpdatedAtUtc = now,
                    });
                }
                else if (selection.Source != EnergySource.VendorWindow || selection.AttributeId is not null)
                {
                    selection.Source = EnergySource.VendorWindow;
                    selection.AttributeId = null;
                    selection.UpdatedAtUtc = now;
                }
            }

            accepted.Add(endpointId);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var rows = accepted
            .SelectMany(id => EnergyWindowMath.Spread(byEndpoint[id])
                .Select(h => new EnergyRow(id, h.HourStartUtc, h.Wh, EnergySource.VendorWindow, h.Estimated)))
            .ToList();
        if (rows.Count > 0)
        {
            var connectionString = connections.GetConnectionString()
                ?? throw new InvalidOperationException("No database connection is configured.");
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await HistorySql.UpsertEnergyAsync(connection, transaction, rows, options.Value.CommandTimeout, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return Done(integrationId, new EnergyWindowWriteResult(accepted.Count, rows.Count, skipped, ignored));
    }

    private static string Address(string externalId, string endpointKey) => externalId.ToUpperInvariant() + "\n" + endpointKey;

    private EnergyWindowWriteResult Done(Guid integrationId, EnergyWindowWriteResult result)
    {
        LogWritten(integrationId, result.Endpoints, result.Hours, result.SkippedEndpoints, result.IgnoredWindows);
        return result;
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Energy windows of integration {IntegrationId}: {Hours} hours on {Endpoints} endpoints written, {Skipped} endpoints with another energy source skipped, {Ignored} windows ignored")]
    private partial void LogWritten(Guid integrationId, int endpoints, int hours, int skipped, int ignored);
}

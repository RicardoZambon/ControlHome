using System.Text.Json;
using ControlHome.Application.Abstractions;
using ControlHome.Application.Auditing;
using ControlHome.Application.Common;
using ControlHome.Application.Devices;
using ControlHome.Application.Integrations;
using ControlHome.Application.Placements;
using ControlHome.Application.Realtime;
using ControlHome.Connectors.Abstractions;
using ControlHome.Domain.Devices;
using ControlHome.Domain.Homes;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Application.Demo;

/// <summary>What <see cref="DemoHomeService.LoadAsync"/> created.</summary>
public sealed record DemoHomeSummary(int Floors, int Rooms, int Devices, int Placements);

/// <summary>
/// Loads <see cref="DemoHome"/> into an empty home: its floors and rooms, a Simulated integration with the <c>demo</c>
/// preset whose devices are imported right away (the connector's static inventory) so they can be placed, named and
/// favorited before the runtime first runs, then starts the integration. Names follow the home's language.
/// </summary>
public sealed class DemoHomeService(
    IControlHomeDb db,
    ConnectorCatalog connectors,
    IDeviceInventoryAdmin inventory,
    IIntegrationRuntimeControl runtime,
    IRealtimePublisher publisher,
    IAuditLog auditLog,
    TimeProvider timeProvider)
{
    public const string DemoTarget = "demo";

    /// <summary>
    /// Errors: 409 <c>demo.not_empty</c> when the home already has floors, integrations or devices (nothing is changed); 404
    /// <c>connectors.not_found</c> without the Simulated connector.
    /// </summary>
    /// <param name="locale"><c>pt-BR</c> for Portuguese names; anything else is English.</param>
    public async Task<Result<DemoHomeSummary>> LoadAsync(string? locale, CancellationToken cancellationToken)
    {
        if (await db.Floors.AnyAsync(cancellationToken)
            || await db.Integrations.AnyAsync(cancellationToken)
            || await db.Devices.AnyAsync(cancellationToken))
        {
            return AppError.Conflict(ErrorCodes.DemoNotEmpty, "The demo home can only be loaded into an empty home.");
        }

        if (connectors.Find(DemoHome.ConnectorKey) is not IStaticInventorySource source)
        {
            return AppError.NotFound(ErrorCodes.ConnectorsNotFound, "The Simulated connector is not available in this version.");
        }

        var portuguese = string.Equals(locale, "pt-BR", StringComparison.OrdinalIgnoreCase);
        var now = timeProvider.GetUtcNow();

        var floors = new Dictionary<string, Floor>(StringComparer.Ordinal);
        foreach (var demoFloor in DemoHome.Floors)
        {
            var floor = new Floor
            {
                Name = portuguese ? demoFloor.NamePt : demoFloor.NameEn,
                Level = demoFloor.Level,
                WidthCm = demoFloor.WidthCm,
                HeightCm = demoFloor.HeightCm,
                GridCm = Floor.DefaultGridCm,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Rooms =
                [
                    .. demoFloor.Rooms.Select((room, index) => new Room
                    {
                        Name = portuguese ? room.NamePt : room.NameEn,
                        Icon = room.Icon,
                        ColorToken = room.ColorToken,
                        Polygon = [.. room.Polygon],
                        SortOrder = index,
                    }),
                ],
            };
            floors[demoFloor.Key] = floor;
            db.Floors.Add(floor);
        }

        var integration = new Integration
        {
            Id = Guid.CreateVersion7(),
            ConnectorKey = DemoHome.ConnectorKey,
            Name = portuguese ? DemoHome.IntegrationNamePt : DemoHome.IntegrationNameEn,
            ConfigJson = DemoHome.IntegrationConfigJson,
            Status = IntegrationStatus.Connecting,
            Enabled = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.Integrations.Add(integration);
        await db.SaveChangesAsync(cancellationToken);

        using (var config = JsonDocument.Parse(DemoHome.IntegrationConfigJson))
        {
            foreach (var device in source.DescribeDevices(config.RootElement))
            {
                await inventory.UpsertAsync(integration.Id, device, cancellationToken);
            }
        }

        var devices = await db.Devices.Include(d => d.Endpoints).Where(d => d.IntegrationId == integration.Id).ToListAsync(cancellationToken);
        var byExternalId = devices.ToDictionary(d => d.ExternalId, StringComparer.Ordinal);
        var placed = new HashSet<Guid>();
        foreach (var demoPlacement in DemoHome.Placements)
        {
            var device = byExternalId[demoPlacement.ExternalId];
            var floor = floors[demoPlacement.FloorKey];
            var roomId = RoomLocator.Find(floor.Rooms, demoPlacement.XCm, demoPlacement.YCm)?.Id;
            var placement = new Placement { FloorId = floor.Id, XCm = demoPlacement.XCm, YCm = demoPlacement.YCm };
            if (demoPlacement.EndpointKey is { } key)
            {
                var endpoint = device.Endpoints.Single(e => e.Key == key);
                placement.EndpointId = endpoint.Id;
                endpoint.RoomId = roomId;
            }
            else
            {
                placement.DeviceId = device.Id;
            }

            // The device's own room is where it is (first) placed.
            if (placed.Add(device.Id))
            {
                device.RoomId = roomId;
            }

            db.Placements.Add(placement);
        }

        foreach (var (externalId, endpointKey) in DemoHome.Favorites)
        {
            byExternalId[externalId].Endpoints.Single(e => e.Key == endpointKey).Favorite = true;
        }

        if (portuguese)
        {
            foreach (var device in devices)
            {
                if (DemoHome.PortugueseDeviceNames.TryGetValue(device.ExternalId, out var name))
                {
                    device.Name = name;
                }

                foreach (var endpoint in device.Endpoints)
                {
                    if (DemoHome.PortugueseEndpointNames.TryGetValue((device.ExternalId, endpoint.Key), out var endpointName))
                    {
                        endpoint.Name = endpointName;
                    }
                }
            }
        }

        var summary = new DemoHomeSummary(floors.Count, floors.Values.Sum(f => f.Rooms.Count), devices.Count, DemoHome.Placements.Count);
        await db.SaveChangesAsync(cancellationToken);
        await auditLog.WriteAsync(
            new AuditEvent(
                AuditActions.DemoLoaded,
                DemoTarget,
                null,
                new { summary.Floors, summary.Rooms, summary.Devices, summary.Placements, Locale = portuguese ? "pt-BR" : "en" }),
            cancellationToken);
        await runtime.StartAsync(integration.Id, cancellationToken);
        await publisher.DevicesChangedAsync(cancellationToken);
        return summary;
    }
}

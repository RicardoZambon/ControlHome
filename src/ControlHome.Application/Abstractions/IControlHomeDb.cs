using ControlHome.Domain.Auditing;
using ControlHome.Domain.Devices;
using ControlHome.Domain.Files;
using ControlHome.Domain.History;
using ControlHome.Domain.Homes;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Application.Abstractions;

/// <summary>The application's port to the database: DbSets plus SaveChanges (no repository/unit-of-work layers).</summary>
public interface IControlHomeDb
{
    DbSet<Home> Homes { get; }

    DbSet<AuditLogEntry> AuditLog { get; }

    DbSet<Floor> Floors { get; }

    DbSet<Room> Rooms { get; }

    DbSet<StoredFile> StoredFiles { get; }

    DbSet<Integration> Integrations { get; }

    DbSet<Device> Devices { get; }

    DbSet<Endpoint> Endpoints { get; }

    DbSet<EndpointCapability> EndpointCapabilities { get; }

    /// <summary>Attributes; <see cref="DeviceAttribute.Id"/> is the <c>AttributeId</c>.</summary>
    DbSet<DeviceAttribute> DeviceAttributes { get; }

    DbSet<AttributeState> AttributeStates { get; }

    /// <summary>Raw history of recorded attributes (keyless: query only; the write-behind appends).</summary>
    DbSet<StateLogEntry> StateLog { get; }

    /// <summary>Momentary events (keyless: query only; the write-behind appends).</summary>
    DbSet<DeviceEvent> DeviceEvents { get; }

    DbSet<ShortTermStatistic> ShortTermStatistics { get; }

    DbSet<HourlyStatistic> HourlyStatistics { get; }

    DbSet<RollupProgress> RollupProgress { get; }

    DbSet<EnergyHourly> EnergyHourly { get; }

    DbSet<EnergySourceSelection> EnergySourceSelections { get; }

    DbSet<RetentionSettings> RetentionSettings { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

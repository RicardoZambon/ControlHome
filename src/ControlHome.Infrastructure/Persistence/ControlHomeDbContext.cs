using ControlHome.Application.Abstractions;
using ControlHome.Application.Identity;
using ControlHome.Domain.Auditing;
using ControlHome.Domain.Devices;
using ControlHome.Domain.Files;
using ControlHome.Domain.Homes;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Infrastructure.Persistence;

/// <summary>
/// The application's database: app tables in schema <c>ch</c>, ASP.NET Core Identity in schema <c>auth</c> (tables without
/// the <c>AspNet</c> prefix), Data Protection keys in <c>ch.DataProtectionKeys</c>.
/// </summary>
public sealed class ControlHomeDbContext(DbContextOptions<ControlHomeDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options), IControlHomeDb, IDataProtectionKeyContext
{
    public const string AppSchema = "ch";
    public const string AuthSchema = "auth";
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    public DbSet<Home> Homes => Set<Home>();

    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    public DbSet<Floor> Floors => Set<Floor>();

    public DbSet<Room> Rooms => Set<Room>();

    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();

    public DbSet<Integration> Integrations => Set<Integration>();

    public DbSet<Device> Devices => Set<Device>();

    public DbSet<Endpoint> Endpoints => Set<Endpoint>();

    public DbSet<EndpointCapability> EndpointCapabilities => Set<EndpointCapability>();

    public DbSet<DeviceAttribute> DeviceAttributes => Set<DeviceAttribute>();

    public DbSet<AttributeState> AttributeStates => Set<AttributeState>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasDefaultSchema(AppSchema);
        builder.ApplyConfigurationsFromAssembly(typeof(ControlHomeDbContext).Assembly);
    }
}

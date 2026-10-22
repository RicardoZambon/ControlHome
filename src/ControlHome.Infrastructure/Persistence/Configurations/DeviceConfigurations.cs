using ControlHome.Domain.Devices;
using ControlHome.Domain.Homes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlHome.Infrastructure.Persistence.Configurations;

// The device model: Integration ─< Device ─< Endpoint ─< EndpointCapability ─< DeviceAttribute (AttributeId) ─ AttributeState.
// Deleting an integration cascades down the whole chain. The room references (Device.RoomId, Endpoint.RoomId) and the
// parent device do NOT cascade in the database (SQL Server allows only one cascade path to a table, and none on a
// self-reference): FloorService clears room references before rooms are deleted.

internal sealed class IntegrationConfiguration : IEntityTypeConfiguration<Integration>
{
    public void Configure(EntityTypeBuilder<Integration> builder)
    {
        builder.ToTable("Integrations");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.ConnectorKey).HasMaxLength(DeviceLimits.ConnectorKeyMaxLength).IsUnicode(false).IsRequired();
        builder.Property(i => i.Name).HasMaxLength(DeviceLimits.NameMaxLength).IsRequired();
        builder.Property(i => i.ConfigJson).HasColumnType("json").IsRequired();
        builder.Property(i => i.SecretsProtected).HasColumnType("nvarchar(max)");
        builder.Property(i => i.Status).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
        builder.Property(i => i.StatusMessage).HasMaxLength(DeviceLimits.StatusMessageMaxLength);

        builder.HasMany(i => i.Devices)
            .WithOne()
            .HasForeignKey(d => d.IntegrationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(i => i.ConnectorKey);
    }
}

internal sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> builder)
    {
        builder.ToTable("Devices");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.ExternalId).HasMaxLength(DeviceLimits.ExternalIdMaxLength).IsRequired();
        builder.Property(d => d.Name).HasMaxLength(DeviceLimits.NameMaxLength).IsRequired();
        builder.Property(d => d.DeviceClass).HasMaxLength(DeviceLimits.DeviceClassMaxLength).IsUnicode(false);
        builder.Property(d => d.Manufacturer).HasMaxLength(DeviceLimits.ManufacturerMaxLength);
        builder.Property(d => d.Model).HasMaxLength(DeviceLimits.ModelMaxLength);
        builder.Property(d => d.Firmware).HasMaxLength(DeviceLimits.FirmwareMaxLength);
        builder.Property(d => d.ConnectionsJson).HasColumnType("json").IsRequired();
        builder.Property(d => d.Availability).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
        builder.Property(d => d.Icon).HasMaxLength(DeviceLimits.IconMaxLength).IsUnicode(false);

        builder.HasIndex(d => new { d.IntegrationId, d.ExternalId }).IsUnique();

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(d => d.ParentDeviceId)
            .OnDelete(DeleteBehavior.ClientSetNull);

        builder.HasOne<Room>()
            .WithMany()
            .HasForeignKey(d => d.RoomId)
            .OnDelete(DeleteBehavior.ClientSetNull);

        builder.HasMany(d => d.Endpoints)
            .WithOne()
            .HasForeignKey(e => e.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class EndpointConfiguration : IEntityTypeConfiguration<Endpoint>
{
    public void Configure(EntityTypeBuilder<Endpoint> builder)
    {
        builder.ToTable("Endpoints");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Key).HasMaxLength(DeviceLimits.EndpointKeyMaxLength).IsRequired();
        builder.Property(e => e.Name).HasMaxLength(DeviceLimits.NameMaxLength);
        builder.Property(e => e.Icon).HasMaxLength(DeviceLimits.IconMaxLength).IsUnicode(false);

        builder.HasIndex(e => new { e.DeviceId, e.Key }).IsUnique();

        builder.HasOne<Room>()
            .WithMany()
            .HasForeignKey(e => e.RoomId)
            .OnDelete(DeleteBehavior.ClientSetNull);

        builder.HasMany(e => e.Capabilities)
            .WithOne()
            .HasForeignKey(c => c.EndpointId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class EndpointCapabilityConfiguration : IEntityTypeConfiguration<EndpointCapability>
{
    public void Configure(EntityTypeBuilder<EndpointCapability> builder)
    {
        builder.ToTable("EndpointCapabilities");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).UseIdentityColumn();
        builder.Property(c => c.CapabilityId).HasMaxLength(DeviceLimits.CapabilityIdMaxLength).IsUnicode(false).IsRequired();
        builder.Property(c => c.ConstraintsJson).HasColumnType("json");
        builder.Property(c => c.GenericDefinitionJson).HasColumnType("json");

        // A capability at most once per endpoint: state reports address (endpoint, capability, attribute).
        builder.HasIndex(c => new { c.EndpointId, c.CapabilityId }).IsUnique();
        builder.HasIndex(c => c.CapabilityId);

        builder.HasMany(c => c.Attributes)
            .WithOne()
            .HasForeignKey(a => a.EndpointCapabilityId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DeviceAttributeConfiguration : IEntityTypeConfiguration<DeviceAttribute>
{
    public void Configure(EntityTypeBuilder<DeviceAttribute> builder)
    {
        builder.ToTable("DeviceAttributes");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).UseIdentityColumn(); // the AttributeId
        builder.Property(a => a.Key).HasMaxLength(DeviceLimits.AttributeKeyMaxLength).IsRequired();

        builder.HasIndex(a => new { a.EndpointCapabilityId, a.Key }).IsUnique();

        builder.HasOne(a => a.State)
            .WithOne()
            .HasForeignKey<AttributeState>(s => s.AttributeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AttributeStateConfiguration : IEntityTypeConfiguration<AttributeState>
{
    public void Configure(EntityTypeBuilder<AttributeState> builder)
    {
        builder.ToTable("AttributeState");
        builder.HasKey(s => s.AttributeId);
        builder.Property(s => s.AttributeId).ValueGeneratedNever();
        builder.Property(s => s.Num).HasColumnType("float");
        builder.Property(s => s.Str).HasMaxLength(AttributeState.MaxStringLength);
        builder.Property(s => s.Json).HasColumnType("json");
        builder.Property(s => s.Kind).HasColumnType("tinyint");
    }
}

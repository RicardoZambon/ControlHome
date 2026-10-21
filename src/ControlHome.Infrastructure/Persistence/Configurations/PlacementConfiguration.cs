using ControlHome.Domain.Devices;
using ControlHome.Domain.Homes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlHome.Infrastructure.Persistence.Configurations;

// Placements reference a floor and exactly one of an endpoint or a device. Deleting an endpoint (with its device or
// integration) cascades to its placement; the device reference does NOT cascade in the database (SQL Server allows one
// cascade path per table: integration → device → endpoint → placement already is one), so the code that deletes devices
// (integration delete, device replace, test reset) removes their placements first. A floor with placements cannot be
// deleted (FloorService answers 409 floors.not_empty), so that reference does not cascade either.
internal sealed class PlacementConfiguration : IEntityTypeConfiguration<Placement>
{
    public void Configure(EntityTypeBuilder<Placement> builder)
    {
        builder.ToTable(
            "Placements",
            table => table.HasCheckConstraint(
                "CK_Placements_Target",
                "([EndpointId] IS NOT NULL AND [DeviceId] IS NULL) OR ([EndpointId] IS NULL AND [DeviceId] IS NOT NULL)"));
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).UseIdentityColumn();

        builder.HasOne<Floor>()
            .WithMany()
            .HasForeignKey(p => p.FloorId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Endpoint>()
            .WithMany()
            .HasForeignKey(p => p.EndpointId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(p => p.DeviceId)
            .OnDelete(DeleteBehavior.ClientCascade);

        // Each endpoint and each device is on the plan at most once.
        builder.HasIndex(p => p.EndpointId).IsUnique().HasFilter("[EndpointId] IS NOT NULL");
        builder.HasIndex(p => p.DeviceId).IsUnique().HasFilter("[DeviceId] IS NOT NULL");
        builder.HasIndex(p => p.FloorId);
    }
}

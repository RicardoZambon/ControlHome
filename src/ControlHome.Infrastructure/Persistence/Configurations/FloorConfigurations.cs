using ControlHome.Application.Floors;
using ControlHome.Domain.Homes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlHome.Infrastructure.Persistence.Configurations;

internal sealed class FloorConfiguration : IEntityTypeConfiguration<Floor>
{
    public void Configure(EntityTypeBuilder<Floor> builder)
    {
        builder.ToTable("Floors");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).UseIdentityColumn();
        builder.Property(f => f.Name).HasMaxLength(FloorRules.NameMaxLength).IsRequired();

        // rowversion: bumped by SQL Server on every UPDATE of the row (layout saves always touch UpdatedAtUtc).
        builder.Property(f => f.RowVersion).IsRowVersion();

        // Nullable complex type stored as a native json column (SQL Server 2025).
        builder.ComplexProperty(f => f.Background, background => background.ToJson());

        builder.HasMany(f => f.Rooms)
            .WithOne()
            .HasForeignKey(r => r.FloorId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(f => f.Level);
    }
}

internal sealed class RoomConfiguration : IEntityTypeConfiguration<Room>
{
    public void Configure(EntityTypeBuilder<Room> builder)
    {
        builder.ToTable("Rooms");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).UseIdentityColumn();
        builder.Property(r => r.Name).HasMaxLength(FloorRules.NameMaxLength).IsRequired();
        builder.Property(r => r.Icon).HasMaxLength(FloorRules.IconMaxLength).IsUnicode(false).IsRequired();
        builder.Property(r => r.ColorToken).HasMaxLength(FloorRules.ColorTokenMaxLength).IsUnicode(false).IsRequired();

        // The outline: a complex collection of points in one json column ([{"X":0,"Y":0},...]).
        builder.ComplexCollection(r => r.Polygon, polygon => polygon.ToJson());

        builder.HasIndex(r => new { r.FloorId, r.SortOrder });
    }
}

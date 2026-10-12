using ControlHome.Domain.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlHome.Infrastructure.Persistence.Configurations;

internal sealed class AuditLogEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    public void Configure(EntityTypeBuilder<AuditLogEntry> builder)
    {
        builder.ToTable("AuditLog");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).UseIdentityColumn();
        builder.Property(e => e.ActorType).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
        builder.Property(e => e.ActorId).HasMaxLength(64);
        builder.Property(e => e.ActorName).HasMaxLength(256);
        builder.Property(e => e.Action).HasMaxLength(128).IsUnicode(false).IsRequired();
        builder.Property(e => e.TargetType).HasMaxLength(64);
        builder.Property(e => e.TargetId).HasMaxLength(128);
        builder.Property(e => e.DetailsJson).HasColumnType("json");
        builder.Property(e => e.IpAddress).HasMaxLength(45).IsUnicode(false);

        builder.HasIndex(e => e.OccurredAtUtc);
        builder.HasIndex(e => e.Action);
    }
}

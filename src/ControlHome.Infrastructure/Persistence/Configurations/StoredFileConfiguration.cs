using ControlHome.Domain.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlHome.Infrastructure.Persistence.Configurations;

internal sealed class StoredFileConfiguration : IEntityTypeConfiguration<StoredFile>
{
    public void Configure(EntityTypeBuilder<StoredFile> builder)
    {
        builder.ToTable("StoredFiles");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Property(f => f.Purpose).HasMaxLength(32).IsUnicode(false).IsRequired();
        builder.Property(f => f.ContentType).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(f => f.Sha256).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(f => f.Data).HasColumnType("varbinary(max)").IsRequired();

        builder.HasIndex(f => f.Purpose);
    }
}

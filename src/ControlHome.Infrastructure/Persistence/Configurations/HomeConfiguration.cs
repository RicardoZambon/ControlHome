using ControlHome.Application.Common;
using ControlHome.Domain.Homes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ControlHome.Infrastructure.Persistence.Configurations;

internal sealed class HomeConfiguration : IEntityTypeConfiguration<Home>
{
    public void Configure(EntityTypeBuilder<Home> builder)
    {
        builder.ToTable("Homes", t => t.HasCheckConstraint("CK_Homes_Singleton", $"[Id] = {Home.SingletonId}"));
        builder.HasKey(h => h.Id);
        builder.Property(h => h.Id).ValueGeneratedNever();
        builder.Property(h => h.Name).HasMaxLength(ValidationRules.HomeNameMaxLength).IsRequired();
        builder.Property(h => h.TimeZoneId).HasMaxLength(64).IsRequired();
        builder.Property(h => h.Locale).HasMaxLength(16).IsRequired();
        builder.Property(h => h.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false).IsRequired();

        // Flat shape stored as a native json column (SQL Server 2025, compatibility level 170).
        builder.ComplexProperty(h => h.Tariff, tariff => tariff.ToJson());
    }
}

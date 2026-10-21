using ControlHome.Domain.History;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ControlHome.Infrastructure.Persistence.Configurations;

// History and energy (milestone 1.4): append-heavy tables keyed on AttributeId / EndpointId without foreign keys (writes
// stay cheap and deleting an integration never waits on years of history); the retention job removes rows whose
// attribute or endpoint is gone. Times are UTC datetime2. PAGE compression: EF sets it on the StateLog/DeviceEvent
// indexes; the History migration adds it to the primary keys of the statistics and energy tables.

/// <summary>A <see cref="DateTimeOffset"/> stored as a UTC <c>datetime2</c> (read back with offset zero).</summary>
internal static class UtcDateTime
{
    public static readonly ValueConverter<DateTimeOffset, DateTime> Converter = new(
        value => value.UtcDateTime,
        value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));
}

internal sealed class StateLogEntryConfiguration : IEntityTypeConfiguration<StateLogEntry>
{
    public void Configure(EntityTypeBuilder<StateLogEntry> builder)
    {
        builder.ToTable("StateLog");
        builder.HasNoKey();
        builder.Property(s => s.TsUtc).HasConversion(UtcDateTime.Converter).HasColumnType("datetime2(3)");
        builder.Property(s => s.Num).HasColumnType("float");
        builder.Property(s => s.Str).HasMaxLength(StateLogEntry.MaxStringLength);

        builder.HasIndex(s => new { s.TsUtc, s.AttributeId })
            .HasDatabaseName("CIX_StateLog")
            .IsClustered()
            .UseDataCompression(DataCompressionType.Page);
        builder.HasIndex(s => new { s.AttributeId, s.TsUtc })
            .HasDatabaseName("IX_StateLog_AttributeId_TsUtc")
            .IncludeProperties(s => new { s.Num, s.Str })
            .UseDataCompression(DataCompressionType.Page);
    }
}

internal sealed class DeviceEventConfiguration : IEntityTypeConfiguration<DeviceEvent>
{
    public void Configure(EntityTypeBuilder<DeviceEvent> builder)
    {
        builder.ToTable("DeviceEvent");
        builder.HasNoKey();
        builder.Property(e => e.TsUtc).HasConversion(UtcDateTime.Converter).HasColumnType("datetime2(3)");
        builder.Property(e => e.EventType).HasMaxLength(DeviceEvent.MaxEventTypeLength).IsRequired();
        builder.Property(e => e.Payload).HasColumnType("json");

        builder.HasIndex(e => new { e.TsUtc, e.AttributeId })
            .HasDatabaseName("CIX_DeviceEvent")
            .IsClustered()
            .UseDataCompression(DataCompressionType.Page);
        builder.HasIndex(e => new { e.AttributeId, e.TsUtc })
            .HasDatabaseName("IX_DeviceEvent_AttributeId_TsUtc")
            .IncludeProperties(e => e.EventType)
            .UseDataCompression(DataCompressionType.Page);
    }
}

internal abstract class AttributeStatisticConfiguration<T> : IEntityTypeConfiguration<T>
    where T : AttributeStatistic
{
    protected abstract string Table { get; }

    public void Configure(EntityTypeBuilder<T> builder)
    {
        builder.ToTable(Table);
        builder.HasKey(s => new { s.AttributeId, s.BucketStartUtc });
        builder.Property(s => s.BucketStartUtc).HasConversion(UtcDateTime.Converter).HasColumnType("datetime2(0)");
        builder.Property(s => s.Mean).HasColumnType("float");
        builder.Property(s => s.Min).HasColumnType("float");
        builder.Property(s => s.Max).HasColumnType("float");
        builder.Property(s => s.Sum).HasColumnType("float");
        builder.Property(s => s.LastCounter).HasColumnType("float");
        ConfigureMore(builder);
    }

    protected virtual void ConfigureMore(EntityTypeBuilder<T> builder)
    {
    }
}

internal sealed class ShortTermStatisticConfiguration : AttributeStatisticConfiguration<ShortTermStatistic>
{
    protected override string Table => "StatShortTerm";

    // Range purges and the hourly rollup read whole time windows.
    protected override void ConfigureMore(EntityTypeBuilder<ShortTermStatistic> builder) =>
        builder.HasIndex(s => s.BucketStartUtc).UseDataCompression(DataCompressionType.Page);
}

internal sealed class HourlyStatisticConfiguration : AttributeStatisticConfiguration<HourlyStatistic>
{
    protected override string Table => "StatHourly";
}

internal sealed class RollupProgressConfiguration : IEntityTypeConfiguration<RollupProgress>
{
    public void Configure(EntityTypeBuilder<RollupProgress> builder)
    {
        builder.ToTable("RollupProgress");
        builder.HasKey(p => p.Name);
        builder.Property(p => p.Name).HasMaxLength(RollupProgress.NameMaxLength).IsUnicode(false);
        builder.Property(p => p.WatermarkUtc).HasConversion(UtcDateTime.Converter).HasColumnType("datetime2(0)");
    }
}

internal sealed class EnergyHourlyConfiguration : IEntityTypeConfiguration<EnergyHourly>
{
    public void Configure(EntityTypeBuilder<EnergyHourly> builder)
    {
        builder.ToTable("EnergyHourly");
        builder.HasKey(e => new { e.EndpointId, e.HourStartUtc });
        builder.Property(e => e.HourStartUtc).HasConversion(UtcDateTime.Converter).HasColumnType("datetime2(0)");
        builder.Property(e => e.Wh).HasColumnType("float");
        builder.Property(e => e.Source).HasConversion(EnergySourceConverter.Instance).HasMaxLength(EnergySources.NameMaxLength).IsUnicode(false);

        builder.HasIndex(e => e.HourStartUtc).UseDataCompression(DataCompressionType.Page);
    }
}

internal sealed class EnergySourceSelectionConfiguration : IEntityTypeConfiguration<EnergySourceSelection>
{
    public void Configure(EntityTypeBuilder<EnergySourceSelection> builder)
    {
        builder.ToTable("EnergySourceSelection");
        builder.HasKey(s => s.EndpointId);
        builder.Property(s => s.EndpointId).ValueGeneratedNever();
        builder.Property(s => s.Source).HasConversion(EnergySourceConverter.Instance).HasMaxLength(EnergySources.NameMaxLength).IsUnicode(false);
    }
}

internal sealed class RetentionSettingsConfiguration : IEntityTypeConfiguration<RetentionSettings>
{
    public void Configure(EntityTypeBuilder<RetentionSettings> builder)
    {
        builder.ToTable("RetentionSettings", t => t.HasCheckConstraint("CK_RetentionSettings_Singleton", "[Id] = 1"));
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
    }
}

/// <summary>Stores <see cref="EnergySource"/> by its stable name (<c>counter</c>, <c>integratedPower</c>, …).</summary>
internal sealed class EnergySourceConverter() : ValueConverter<EnergySource, string>(
    source => EnergySources.Name(source),
    name => EnergySources.Parse(name))
{
    public static EnergySourceConverter Instance { get; } = new();
}

using Microsoft.EntityFrameworkCore;
using PlantOps.Domain.Assets;
using PlantOps.Domain.Telemetry;

namespace PlantOps.Infrastructure.Persistence;

internal static class TelemetryModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<Asset>().HasAlternateKey(x => new { x.PlantId, x.Id });
        var point = model.Entity<MeasurementPoint>();
        point.HasKey(x => x.Id);
        point.HasAlternateKey(x => new { x.PlantId, x.Id });
        point.HasAlternateKey(x => new { x.PlantId, x.AssetId, x.Id });
        point.HasOne<Asset>().WithMany().HasForeignKey(x => new { x.PlantId, x.AssetId })
            .HasPrincipalKey(x => new { x.PlantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        point.HasIndex(x => new { x.AssetId, x.Code }).IsUnique();
        point.Property(x => x.Code).HasMaxLength(64);
        point.Property(x => x.Name).HasMaxLength(200);
        point.Property(x => x.Quantity).HasMaxLength(64);
        point.Property(x => x.CanonicalUnit).HasMaxLength(32);
        point.Property(x => x.ValueType).HasMaxLength(32);
        point.Property(x => x.Version).IsConcurrencyToken();
        point.ToTable("MeasurementPoints", t => t.HasCheckConstraint("CK_Point_Intervals", "\"ExpectedIntervalMs\" BETWEEN 1000 AND 60000 AND \"StaleAfterMs\" BETWEEN \"ExpectedIntervalMs\" AND 300000"));
        var source = model.Entity<DataSource>();
        source.HasKey(x => x.Id);
        source.HasAlternateKey(x => new { x.PlantId, x.Id });
        source.HasOne<Plant>().WithMany().HasForeignKey(x => x.PlantId).OnDelete(DeleteBehavior.Restrict);
        source.Property(x => x.Name).HasMaxLength(200);
        source.Property(x => x.Kind).HasMaxLength(32);
        source.Property(x => x.ConnectionParameters).HasColumnType("jsonb");
        source.Property(x => x.Version).IsConcurrencyToken();
        var mapping = model.Entity<SourceMapping>();
        mapping.HasKey(x => x.Id);
        mapping.HasAlternateKey(x => new { x.PlantId, x.PointId, x.Id });
        mapping.HasOne<DataSource>().WithMany().HasForeignKey(x => new { x.PlantId, x.DataSourceId })
            .HasPrincipalKey(x => new { x.PlantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        mapping.HasOne<MeasurementPoint>().WithMany().HasForeignKey(x => new { x.PlantId, x.PointId })
            .HasPrincipalKey(x => new { x.PlantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        mapping.HasIndex(x => x.PointId).IsUnique().HasFilter("\"Enabled\" = TRUE");
        mapping.Property(x => x.SourceKey).HasMaxLength(128);
        mapping.Property(x => x.RawUnit).HasMaxLength(32);
        mapping.Property(x => x.SourceParameters).HasColumnType("jsonb");
        mapping.Property(x => x.Version).IsConcurrencyToken();
        var reading = model.Entity<MeasurementReading>();
        reading.HasKey(x => new { x.EffectiveDate, x.Id });
        reading.HasOne<MeasurementPoint>().WithMany().HasForeignKey(x => new { x.PlantId, x.AssetId, x.PointId })
            .HasPrincipalKey(x => new { x.PlantId, x.AssetId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        reading.HasOne<SourceMapping>().WithMany().HasForeignKey(x => new { x.PlantId, x.PointId, x.SourceMappingId })
            .HasPrincipalKey(x => new { x.PlantId, x.PointId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        reading.HasOne<DataSource>().WithMany().HasForeignKey(x => new { x.PlantId, x.DataSourceId })
            .HasPrincipalKey(x => new { x.PlantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        reading.HasIndex(x => new { x.EffectiveDate, x.PointId, x.SourceEventKey }).IsUnique();
        reading.HasIndex(x => new { x.PointId, x.EffectiveAt });
        reading.Property(x => x.CanonicalUnit).HasMaxLength(32);
        reading.Property(x => x.RawUnit).HasMaxLength(32);
        reading.Property(x => x.SourceKind).HasMaxLength(32);
        reading.Property(x => x.Quality).HasMaxLength(32);
        reading.Property(x => x.StatusSymbol).HasMaxLength(128);
        reading.Property(x => x.TimestampOrigin).HasMaxLength(16);
        reading.Property(x => x.SourceEventKey).HasMaxLength(200);
        reading.Property(x => x.RawValueJson).HasColumnType("jsonb");
        reading.Property(x => x.RawStatusCode).HasConversion(v => v.HasValue ? (long?)v.Value : null, v => v.HasValue ? (uint?)v.Value : null);
        reading.ToTable("MeasurementReadings", t => t.HasCheckConstraint("CK_Reading_Quality",
            "\"EffectiveDate\" = (\"EffectiveAt\" AT TIME ZONE 'UTC')::date AND \"Quality\" IN ('Good','Uncertain','Bad') AND (\"NumericValue\" IS NULL OR (\"NumericValue\" <> 'NaN'::double precision AND \"NumericValue\" <> 'Infinity'::double precision AND \"NumericValue\" <> '-Infinity'::double precision))"));
        var latest = model.Entity<PointLatestState>();
        latest.HasKey(x => x.PointId);
        latest.Property(x => x.Version).IsConcurrencyToken();
        latest.Property(x => x.LatestObservedQuality).HasMaxLength(32);
        latest.HasOne<MeasurementPoint>().WithOne().HasForeignKey<PointLatestState>(x => x.PointId).OnDelete(DeleteBehavior.Restrict);
        var minute = model.Entity<MeasurementMinute>();
        minute.HasKey(x => new { x.PointId, x.BucketStart });
        minute.HasOne<MeasurementPoint>().WithMany().HasForeignKey(x => x.PointId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<DirtyMeasurementMinute>().HasKey(x => new { x.PointId, x.BucketStart });
        model.Entity<MeasurementPartition>().HasKey(x => x.Date);
        var gap = model.Entity<IngestionGap>();
        gap.HasKey(x => x.Id);
        gap.Property(x => x.Reason).HasMaxLength(64);
        gap.HasOne<MeasurementPoint>().WithMany().HasForeignKey(x => x.PointId).OnDelete(DeleteBehavior.Restrict);
        gap.HasIndex(x => new { x.PointId, x.From });
    }
}

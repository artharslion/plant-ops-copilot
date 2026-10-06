using Microsoft.EntityFrameworkCore;
using PlantOps.Domain.Alarms;
using PlantOps.Domain.Telemetry;

namespace PlantOps.Infrastructure.Persistence;

internal static class AlarmModel
{
    public static void Configure(ModelBuilder model)
    {
        var rule = model.Entity<AlarmRule>();
        rule.HasKey(x => x.Id);
        rule.HasAlternateKey(x => new { x.PlantId, x.PointId, x.Id });
        rule.HasOne<MeasurementPoint>().WithMany().HasForeignKey(x => new { x.PlantId, x.PointId })
            .HasPrincipalKey(x => new { x.PlantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        rule.Property(x => x.Name).HasMaxLength(200);
        rule.Property(x => x.RuleType).HasMaxLength(32);
        rule.Property(x => x.Severity).HasMaxLength(32);
        rule.Property(x => x.Version).IsConcurrencyToken();
        rule.HasIndex(x => x.PointId);
        rule.ToTable("AlarmRules", t => t.HasCheckConstraint("CK_Rule_Threshold",
            "\"ClearBelow\" < \"RaiseAbove\" AND \"RaiseAbove\" > '-Infinity'::double precision AND \"RaiseAbove\" < 'Infinity'::double precision AND \"ClearBelow\" > '-Infinity'::double precision AND \"ClearBelow\" < 'Infinity'::double precision AND \"RaiseForMs\" BETWEEN 0 AND 300000 AND \"ClearForMs\" BETWEEN 0 AND 300000 AND \"MaxSampleGapMs\" BETWEEN 1 AND 300000 AND \"Severity\" IN ('Low','Medium','High') AND \"RuleType\" = 'HighThreshold'"));
        var runtime = model.Entity<RuleRuntimeState>();
        runtime.HasKey(x => x.RuleId);
        runtime.HasOne<AlarmRule>().WithOne().HasForeignKey<RuleRuntimeState>(x => x.RuleId).OnDelete(DeleteBehavior.Restrict);
        runtime.Property(x => x.Version).IsConcurrencyToken();
        var alarm = model.Entity<Alarm>();
        alarm.HasKey(x => x.Id);
        alarm.HasOne<MeasurementPoint>().WithMany().HasForeignKey(x => new { x.PlantId, x.AssetId, x.PointId })
            .HasPrincipalKey(x => new { x.PlantId, x.AssetId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        alarm.HasOne<AlarmRule>().WithMany().HasForeignKey(x => new { x.PlantId, x.PointId, x.RuleId })
            .HasPrincipalKey(x => new { x.PlantId, x.PointId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        alarm.Property(x => x.State).HasMaxLength(32);
        alarm.Property(x => x.Severity).HasMaxLength(32);
        alarm.Property(x => x.AcknowledgedByUserId).HasMaxLength(450);
        alarm.Property(x => x.ThresholdSnapshot).HasColumnType("jsonb");
        alarm.Property(x => x.Version).IsConcurrencyToken();
        alarm.HasIndex(x => x.RuleId).IsUnique().HasFilter("\"State\" = 'Active'");
        alarm.HasIndex(x => new { x.PlantId, x.StartedAt, x.Id });
        alarm.ToTable("Alarms", t => t.HasCheckConstraint("CK_Alarm_State", "\"State\" IN ('Active','Recovered')"));
        var evt = model.Entity<AlarmEvent>();
        evt.HasKey(x => x.Id);
        evt.HasOne<Alarm>().WithMany().HasForeignKey(x => x.AlarmId).OnDelete(DeleteBehavior.Restrict);
        evt.Property(x => x.EventType).HasMaxLength(32);
        evt.Property(x => x.ActorUserId).HasMaxLength(450);
        evt.Property(x => x.CorrelationId).HasMaxLength(200);
        evt.HasIndex(x => new { x.AlarmId, x.OccurredAt });
    }
}

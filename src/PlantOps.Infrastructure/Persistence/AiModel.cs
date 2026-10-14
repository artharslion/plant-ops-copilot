using Microsoft.EntityFrameworkCore;
using PlantOps.Domain.AI;
using PlantOps.Domain.Alarms;
using PlantOps.Domain.Assets;

namespace PlantOps.Infrastructure.Persistence;

internal static class AiModel
{
    public static void Configure(ModelBuilder model)
    {
        var run = model.Entity<AiRun>();
        run.HasKey(x => x.Id);
        run.Property(x => x.ActorUserId).HasMaxLength(450);
        run.Property(x => x.CorrelationId).HasMaxLength(200);
        run.Property(x => x.Status).HasMaxLength(32);
        run.Property(x => x.Provider).HasMaxLength(32);
        run.Property(x => x.Model).HasMaxLength(200);
        run.Property(x => x.PromptVersion).HasMaxLength(64);
        run.Property(x => x.FailureCode).HasMaxLength(64);
        run.HasOne<Asset>().WithMany().HasForeignKey(x => new { x.PlantId, x.AssetId }).HasPrincipalKey(x => new { x.PlantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        run.HasOne<Alarm>().WithMany().HasForeignKey(x => x.AlarmId).OnDelete(DeleteBehavior.Restrict);
        run.HasIndex(x => new { x.Status, x.CreatedAt });
        run.HasIndex(x => new { x.ActorUserId, x.AlarmId, x.CreatedAt });
        run.ToTable("AiRuns", t => t.HasCheckConstraint("CK_AiRun_Status", "\"Status\" IN ('Pending','Running','Succeeded','Failed','Cancelled') AND \"Attempts\" BETWEEN 0 AND 2"));
    }
}

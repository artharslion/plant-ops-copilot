using Microsoft.EntityFrameworkCore;
using PlantOps.Domain.AI;
using PlantOps.Domain.Alarms;
using PlantOps.Domain.Assets;
using PlantOps.Domain.Maintenance;
using PlantOps.Infrastructure.Identity;

namespace PlantOps.Infrastructure.Persistence;

internal static class AiModel
{
    public static void Configure(ModelBuilder model)
    {
        var settings = model.Entity<AiRuntimeSetting>();
        settings.HasKey(x => x.Id);
        settings.Property(x => x.Provider).HasMaxLength(32);
        settings.Property(x => x.Version).IsConcurrencyToken();
        settings.ToTable("AiRuntimeSettings", table => table.HasCheckConstraint("CK_AiRuntimeSettings_Singleton",
            "\"Id\" = 1 AND \"Provider\" IN ('Fake','OpenAiCompatible') AND \"Version\" > 0"));

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

        var proposal = model.Entity<ActionProposal>();
        proposal.HasKey(x => x.Id);
        proposal.HasAlternateKey(x => new { x.PlantId, x.AssetId, x.Id });
        proposal.HasOne<AiRun>().WithMany().HasForeignKey(x => x.AiRunId).OnDelete(DeleteBehavior.Restrict);
        proposal.HasOne<Asset>().WithMany().HasForeignKey(x => new { x.PlantId, x.AssetId })
            .HasPrincipalKey(x => new { x.PlantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        proposal.HasOne<Alarm>().WithMany().HasForeignKey(x => new { x.PlantId, x.AssetId, x.AlarmId })
            .HasPrincipalKey(x => new { x.PlantId, x.AssetId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        proposal.HasOne<PlantOpsUser>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<WorkOrder>().HasAlternateKey(x => new { x.PlantId, x.AssetId, x.Id });
        proposal.HasOne<WorkOrder>().WithMany().HasForeignKey(x => new { x.PlantId, x.AssetId, x.TargetWorkOrderId })
            .HasPrincipalKey(x => new { x.PlantId, x.AssetId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        model.Entity<WorkOrderNote>().HasOne<ActionProposal>().WithMany().HasForeignKey(x => x.AiProposalId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<WorkOrderNote>().HasIndex(x => x.AiProposalId).IsUnique();
        proposal.Property(x => x.OwnerUserId).HasMaxLength(450);
        proposal.Property(x => x.ActionType).HasMaxLength(32);
        proposal.Property(x => x.DraftOrigin).HasMaxLength(32);
        proposal.Property(x => x.Status).HasMaxLength(32);
        proposal.Property(x => x.PayloadHash).HasMaxLength(64);
        proposal.Property(x => x.InvestigationHash).HasMaxLength(64);
        proposal.Property(x => x.ApprovalCorrelationId).HasMaxLength(200);
        proposal.Property(x => x.ExecutionKey).HasMaxLength(128);
        proposal.Property(x => x.Version).IsConcurrencyToken();
        proposal.HasIndex(x => new { x.OwnerUserId, x.AiRunId, x.CreatedAt });
        proposal.ToTable("ActionProposals", t => t.HasCheckConstraint("CK_ActionProposal_State",
            "\"Status\" IN ('Pending','Rejected','Executed','Expired','Invalidated') AND ((\"ActionType\" = 'CreateWorkOrder' AND \"TargetWorkOrderId\" IS NULL AND \"TargetWorkOrderVersion\" IS NULL) OR (\"ActionType\" = 'AppendWorkOrderNote' AND \"TargetWorkOrderId\" IS NOT NULL AND \"TargetWorkOrderVersion\" > 0 AND \"TargetWorkOrderVersion\" IS NOT NULL)) AND \"DraftOrigin\" = 'InvestigationTemplate' AND \"Version\" > 0 AND \"ExpiresAt\" > \"CreatedAt\" AND ((\"Status\" = 'Executed' AND \"ResultEntityId\" IS NOT NULL AND \"ExecutedAt\" IS NOT NULL AND \"ExecutionKey\" IS NOT NULL) OR (\"Status\" <> 'Executed' AND \"ResultEntityId\" IS NULL AND \"ExecutedAt\" IS NULL AND \"ExecutionKey\" IS NULL))"));
        model.Entity<PlantOps.Domain.Maintenance.WorkOrder>().HasOne<ActionProposal>().WithMany()
            .HasForeignKey(x => new { x.PlantId, x.AssetId, x.AiProposalId })
            .HasPrincipalKey(x => new { x.PlantId, x.AssetId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

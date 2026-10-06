using Microsoft.EntityFrameworkCore;
using PlantOps.Domain.Assets;
using PlantOps.Domain.Alarms;
using PlantOps.Domain.Maintenance;

namespace PlantOps.Infrastructure.Persistence;

internal static class MaintenanceModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<Alarm>().HasAlternateKey(x => new { x.PlantId, x.AssetId, x.Id });
        var w = model.Entity<WorkOrder>();
        w.HasKey(x => x.Id);
        w.HasOne<Asset>().WithMany().HasForeignKey(x => new { x.PlantId, x.AssetId }).HasPrincipalKey(x => new { x.PlantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        w.HasOne<Alarm>().WithMany().HasForeignKey(x => new { x.PlantId, x.AssetId, x.AlarmId }).HasPrincipalKey(x => new { x.PlantId, x.AssetId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        w.HasIndex(x => new { x.PlantId, x.Number }).IsUnique();
        w.HasIndex(x => new { x.AssetId, x.Status, x.CreatedAt, x.Id });
        w.HasIndex(x => x.AiProposalId).IsUnique();
        w.Property(x => x.Version).IsConcurrencyToken();
        w.Property(x => x.Number).HasMaxLength(64);
        w.Property(x => x.Title).HasMaxLength(200);
        w.Property(x => x.Description).HasMaxLength(8000);
        w.Property(x => x.Resolution).HasMaxLength(8000);
        w.Property(x => x.Status).HasMaxLength(32);
        w.Property(x => x.Priority).HasMaxLength(32);
        w.Property(x => x.Source).HasMaxLength(32);
        w.Property(x => x.CreatedByUserId).HasMaxLength(450);
        w.Property(x => x.AssignedToUserId).HasMaxLength(450);
        w.ToTable("WorkOrders", t => t.HasCheckConstraint("CK_WorkOrder_State", "\"Status\" IN ('Draft','Open','InProgress','Completed','Closed','Cancelled') AND \"Priority\" IN ('Low','Medium','High') AND (\"Status\" NOT IN ('Completed','Closed') OR length(trim(\"Resolution\")) > 0 AND \"Resolution\" IS NOT NULL)"));
        var n = model.Entity<WorkOrderNote>();
        n.HasKey(x => x.Id);
        n.HasOne<WorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        n.Property(x => x.Text).HasMaxLength(8000);
        n.Property(x => x.Kind).HasMaxLength(32);
        n.Property(x => x.ActorUserId).HasMaxLength(450);
        n.HasIndex(x => new { x.WorkOrderId, x.OccurredAt, x.Id });
    }
}

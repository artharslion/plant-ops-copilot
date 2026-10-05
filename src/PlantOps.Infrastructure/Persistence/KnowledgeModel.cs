using Microsoft.EntityFrameworkCore;
using PlantOps.Domain.Knowledge;
using PlantOps.Domain.Assets;
using PlantOps.Domain.Operations;
namespace PlantOps.Infrastructure.Persistence;
internal static class KnowledgeModel
{
    public static void Configure(ModelBuilder m)
    {
        var d = m.Entity<Document>(); d.HasKey(x => x.Id); d.Property(x => x.Version).IsConcurrencyToken();
        d.Property(x => x.Title).HasMaxLength(200); d.Property(x => x.Category).HasMaxLength(64); d.Property(x => x.Visibility).HasMaxLength(32);
        d.HasOne<Plant>().WithMany().HasForeignKey(x => x.PlantId).OnDelete(DeleteBehavior.Restrict);
        d.HasOne<Asset>().WithMany().HasForeignKey(x => new { x.PlantId, x.AssetId }).HasPrincipalKey(x => new { x.PlantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        d.HasIndex(x => new { x.PlantId, x.AssetId, x.Id });
        var v = m.Entity<DocumentVersion>(); v.HasKey(x => x.Id); v.HasAlternateKey(x => new { x.DocumentId, x.Id });
        v.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
        v.HasIndex(x => new { x.DocumentId, x.VersionNumber }).IsUnique(); v.HasIndex(x => x.StorageKey).IsUnique();
        v.Property(x => x.FileName).HasMaxLength(200); v.Property(x => x.MediaType).HasMaxLength(64); v.Property(x => x.StorageKey).HasMaxLength(64);
        v.Property(x => x.Sha256).HasMaxLength(64); v.Property(x => x.UploadedByUserId).HasMaxLength(450); v.Property(x => x.Status).HasMaxLength(32); v.Property(x => x.IndexStatus).HasMaxLength(32);
        v.ToTable("DocumentVersions", t => t.HasCheckConstraint("CK_DocumentVersion", "\"VersionNumber\" > 0 AND \"SizeBytes\" BETWEEN 1 AND 10485760 AND \"Status\" IN ('Draft','Published','Withdrawn')"));
        d.ToTable("Documents", t => t.HasCheckConstraint("CK_DocumentVisibility", "\"Visibility\" IN ('PlantReaders','RestrictedRoles')"));
        var o = m.Entity<OutboxEvent>(); o.HasKey(x => x.Id); o.Property(x => x.EventType).HasMaxLength(64); o.HasIndex(x => new { x.AvailableAt, x.OccurredAt }).HasFilter("\"DispatchedAt\" IS NULL");
    }
}

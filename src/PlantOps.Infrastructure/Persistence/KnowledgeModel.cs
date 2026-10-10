using Microsoft.EntityFrameworkCore;
using PlantOps.Domain.Knowledge;
using PlantOps.Domain.Assets;
using PlantOps.Domain.Operations;

namespace PlantOps.Infrastructure.Persistence;

internal static class KnowledgeModel
{
    public static void Configure(ModelBuilder m)
    {
        var d = m.Entity<Document>();
        d.HasKey(x => x.Id);
        d.Property(x => x.Version).IsConcurrencyToken();
        d.Property(x => x.Title).HasMaxLength(200);
        d.Property(x => x.Category).HasMaxLength(64);
        d.Property(x => x.Visibility).HasMaxLength(32);
        d.HasOne<Plant>().WithMany().HasForeignKey(x => x.PlantId).OnDelete(DeleteBehavior.Restrict);
        d.HasOne<Asset>().WithMany().HasForeignKey(x => new { x.PlantId, x.AssetId }).HasPrincipalKey(x => new { x.PlantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        d.HasIndex(x => new { x.PlantId, x.AssetId, x.Id });
        var v = m.Entity<DocumentVersion>();
        v.HasKey(x => x.Id);
        v.HasAlternateKey(x => new { x.DocumentId, x.Id });
        v.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
        v.HasIndex(x => new { x.DocumentId, x.VersionNumber }).IsUnique();
        v.HasIndex(x => x.StorageKey).IsUnique();
        v.Property(x => x.FileName).HasMaxLength(200);
        v.Property(x => x.MediaType).HasMaxLength(64);
        v.Property(x => x.StorageKey).HasMaxLength(64);
        v.Property(x => x.Sha256).HasMaxLength(64);
        v.Property(x => x.UploadedByUserId).HasMaxLength(450);
        v.Property(x => x.Status).HasMaxLength(32);
        v.Property(x => x.IndexStatus).HasMaxLength(32);
        v.ToTable("DocumentVersions", t => t.HasCheckConstraint("CK_DocumentVersion", "\"VersionNumber\" > 0 AND \"SizeBytes\" BETWEEN 1 AND 10485760 AND \"Status\" IN ('Draft','Published','Withdrawn')"));
        d.ToTable("Documents", t => t.HasCheckConstraint("CK_DocumentVisibility", "\"Visibility\" IN ('PlantReaders','RestrictedRoles')"));
        var c = m.Entity<DocumentChunk>();
        c.HasKey(x => x.Id);
        c.HasOne<DocumentVersion>().WithMany().HasForeignKey(x => new { x.DocumentId, x.VersionId }).HasPrincipalKey(x => new { x.DocumentId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        c.HasIndex(x => new { x.VersionId, x.Ordinal }).IsUnique();
        c.Property(x => x.Section).HasMaxLength(400);
        c.Property(x => x.Text).HasMaxLength(2000);
        c.Property(x => x.Sha256).HasMaxLength(64);
        c.Property(x => x.ParserVersion).HasMaxLength(32);
        c.ToTable("DocumentChunks", t => t.HasCheckConstraint("CK_DocumentChunk", "\"Ordinal\" >= 0 AND \"Ordinal\" < 256 AND \"StartLine\" > 0 AND \"EndLine\" >= \"StartLine\""));
        var o = m.Entity<OutboxEvent>();
        o.HasKey(x => x.Id);
        o.Property(x => x.EventType).HasMaxLength(64);
        o.HasIndex(x => new { x.AvailableAt, x.OccurredAt }).HasFilter("\"DispatchedAt\" IS NULL");
        o.HasIndex(x => new { x.DispatchedAt, x.Id }).HasFilter("\"DispatchedAt\" IS NOT NULL");
    }
}

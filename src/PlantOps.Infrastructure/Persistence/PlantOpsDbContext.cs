using Microsoft.EntityFrameworkCore;
using PlantOps.Domain.Assets;

namespace PlantOps.Infrastructure.Persistence;

public sealed class PlantOpsDbContext(DbContextOptions<PlantOpsDbContext> options) : DbContext(options)
{
    public DbSet<Plant> Plants => Set<Plant>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Asset> Assets => Set<Asset>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var plant = modelBuilder.Entity<Plant>();
        plant.ToTable("Plants");
        plant.HasKey(x => x.Id);
        plant.Property(x => x.Code).HasMaxLength(64);
        plant.Property(x => x.Name).HasMaxLength(200);
        plant.Property(x => x.DisplayTimeZone).HasMaxLength(100);
        plant.Property(x => x.Version).IsConcurrencyToken();
        plant.HasIndex(x => x.Code).IsUnique();

        var location = modelBuilder.Entity<Location>();
        location.ToTable("Locations");
        location.HasKey(x => x.Id);
        location.HasAlternateKey(x => new { x.PlantId, x.Id });
        location.Property(x => x.Code).HasMaxLength(64);
        location.Property(x => x.Name).HasMaxLength(200);
        location.Property(x => x.Version).IsConcurrencyToken();
        location.HasIndex(x => new { x.PlantId, x.Code }).IsUnique();
        location.HasOne<Plant>().WithMany().HasForeignKey(x => x.PlantId).OnDelete(DeleteBehavior.Restrict);
        location.HasOne<Location>().WithMany()
            .HasForeignKey(x => new { x.PlantId, x.ParentLocationId })
            .HasPrincipalKey(x => new { x.PlantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var asset = modelBuilder.Entity<Asset>();
        asset.ToTable("Assets");
        asset.HasKey(x => x.Id);
        asset.Property(x => x.Code).HasMaxLength(64);
        asset.Property(x => x.Name).HasMaxLength(200);
        asset.Property(x => x.AssetType).HasMaxLength(64);
        asset.Property(x => x.LifecycleStatus).HasMaxLength(32);
        asset.Property(x => x.Version).IsConcurrencyToken();
        asset.HasIndex(x => new { x.PlantId, x.Code }).IsUnique();
        asset.HasOne<Location>().WithMany()
            .HasForeignKey(x => new { x.PlantId, x.LocationId })
            .HasPrincipalKey(x => new { x.PlantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

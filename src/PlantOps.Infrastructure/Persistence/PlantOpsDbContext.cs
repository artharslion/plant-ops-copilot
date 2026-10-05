using Microsoft.EntityFrameworkCore;
using PlantOps.Domain.Assets;
using PlantOps.Domain.Identity;
using PlantOps.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using PlantOps.Domain.Telemetry;
using PlantOps.Domain.Alarms;

namespace PlantOps.Infrastructure.Persistence;

public sealed class PlantOpsDbContext(DbContextOptions<PlantOpsDbContext> options) : IdentityDbContext<PlantOpsUser>(options)
{
    public DbSet<Plant> Plants => Set<Plant>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<UserPlantAccess> UserPlantAccess => Set<UserPlantAccess>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<MeasurementPoint> MeasurementPoints => Set<MeasurementPoint>();
    public DbSet<DataSource> DataSources => Set<DataSource>();
    public DbSet<SourceMapping> SourceMappings => Set<SourceMapping>();
    public DbSet<MeasurementReading> MeasurementReadings => Set<MeasurementReading>();
    public DbSet<PointLatestState> PointLatestStates => Set<PointLatestState>();
    public DbSet<AlarmRule> AlarmRules => Set<AlarmRule>();
    public DbSet<RuleRuntimeState> RuleRuntimeStates => Set<RuleRuntimeState>();
    public DbSet<Alarm> Alarms => Set<Alarm>();
    public DbSet<AlarmEvent> AlarmEvents => Set<AlarmEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        TelemetryModel.Configure(modelBuilder);
        AlarmModel.Configure(modelBuilder);
        modelBuilder.Entity<PlantOpsUser>().Property(x => x.DisplayName).HasMaxLength(200);
        var access = modelBuilder.Entity<UserPlantAccess>();
        access.HasKey(x => new { x.UserId, x.PlantId });
        access.HasOne<PlantOpsUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        access.HasOne<Plant>().WithMany().HasForeignKey(x => x.PlantId).OnDelete(DeleteBehavior.Restrict);
        var audit = modelBuilder.Entity<AuditLog>();
        audit.HasKey(x => x.Id);
        audit.Property(x => x.Before).HasColumnType("jsonb");
        audit.Property(x => x.After).HasColumnType("jsonb");
        audit.Property(x => x.Action).HasMaxLength(32);
        audit.Property(x => x.ResourceType).HasMaxLength(32);
        audit.Property(x => x.ActorUserId).HasMaxLength(450);
        audit.Property(x => x.CorrelationId).HasMaxLength(200);
        audit.HasIndex(x => new { x.PlantId, x.OccurredAt });
        var plant = modelBuilder.Entity<Plant>();
        plant.ToTable("Plants");
        plant.HasKey(x => x.Id);
        plant.Property(x => x.Code).HasMaxLength(64);
        plant.Property(x => x.Name).HasMaxLength(200);
        plant.Property(x => x.DisplayTimeZone).HasMaxLength(100);
        plant.Property(x => x.Version).IsConcurrencyToken();
        plant.Property(x => x.Description).HasMaxLength(8000);
        plant.HasIndex(x => x.Code).IsUnique();

        var location = modelBuilder.Entity<Location>();
        location.ToTable("Locations");
        location.HasKey(x => x.Id);
        location.HasAlternateKey(x => new { x.PlantId, x.Id });
        location.Property(x => x.Code).HasMaxLength(64);
        location.Property(x => x.Name).HasMaxLength(200);
        location.Property(x => x.Version).IsConcurrencyToken();
        location.Property(x => x.Description).HasMaxLength(8000);
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
        asset.Property(x => x.Manufacturer).HasMaxLength(200);
        asset.Property(x => x.Model).HasMaxLength(200);
        asset.Property(x => x.SerialNumber).HasMaxLength(200);
        asset.Property(x => x.Criticality).HasMaxLength(32);
        asset.HasIndex(x => new { x.PlantId, x.Code }).IsUnique();
        asset.HasOne<Location>().WithMany()
            .HasForeignKey(x => new { x.PlantId, x.LocationId })
            .HasPrincipalKey(x => new { x.PlantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }

    private void CheckImmutableReadings()
    {
        if (ChangeTracker.Entries<AlarmEvent>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Alarm events are immutable.");
        if (ChangeTracker.Entries<MeasurementReading>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Measurement readings are immutable.");
    }
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    { CheckImmutableReadings(); return base.SaveChanges(acceptAllChangesOnSuccess); }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    { CheckImmutableReadings(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken); }
}

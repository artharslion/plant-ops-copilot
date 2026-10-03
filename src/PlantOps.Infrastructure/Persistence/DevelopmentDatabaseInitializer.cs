using Microsoft.EntityFrameworkCore;
using PlantOps.Domain.Assets;

namespace PlantOps.Infrastructure.Persistence;

public static class DevelopmentDatabaseInitializer
{
    public static async Task InitializeAsync(PlantOpsDbContext db, string environment, CancellationToken cancellationToken = default)
    {
        if (environment != "Development")
            throw new InvalidOperationException("Development database initialization is disabled outside Development.");
        await db.Database.MigrateAsync(cancellationToken);
        // A transaction-scoped lock serializes concurrent development initializations.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7300103)", cancellationToken);

        var plantId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var locationId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var assetId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        if (!await db.Plants.AnyAsync(x => x.Id == plantId, cancellationToken))
            db.Plants.Add(new Plant { Id = plantId, Code = "NORTH", Name = "North Plant" });
        if (!await db.Locations.AnyAsync(x => x.Id == locationId, cancellationToken))
            db.Locations.Add(new Location { Id = locationId, PlantId = plantId, Code = "UTILITIES", Name = "Utilities" });
        if (!await db.Assets.AnyAsync(x => x.Id == assetId, cancellationToken))
            db.Assets.Add(new Asset { Id = assetId, PlantId = plantId, LocationId = locationId,
                Code = "P-101", Name = "Cooling Pump P-101", AssetType = "Pump" });

        var southId = Guid.Parse("10000000-0000-0000-0000-000000000002");
        var southLocation = Guid.Parse("20000000-0000-0000-0000-000000000002");
        if (!await db.Plants.AnyAsync(x => x.Id == southId, cancellationToken))
            db.Plants.Add(new Plant { Id = southId, Code = "SOUTH", Name = "South Plant" });
        if (!await db.Locations.AnyAsync(x => x.Id == southLocation, cancellationToken))
            db.Locations.Add(new Location { Id = southLocation, PlantId = southId, Code = "PROCESS", Name = "Process Hall" });
        var southAsset = Guid.Parse("30000000-0000-0000-0000-000000000002");
        if (!await db.Assets.AnyAsync(x => x.Id == southAsset, cancellationToken))
            db.Assets.Add(new Asset { Id = southAsset, PlantId = southId, LocationId = southLocation,
                Code = "F-201", Name = "Transfer Fan F-201", AssetType = "Fan" });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

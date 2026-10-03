using Microsoft.EntityFrameworkCore;
using PlantOps.Domain.Assets;

namespace PlantOps.Infrastructure.Persistence;

public static class DevelopmentDatabaseInitializer
{
    public static async Task InitializeAsync(PlantOpsDbContext db, CancellationToken cancellationToken = default)
    {
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

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

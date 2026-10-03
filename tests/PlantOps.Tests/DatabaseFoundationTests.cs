using Microsoft.EntityFrameworkCore;
using PlantOps.Domain.Assets;
using PlantOps.Infrastructure.Persistence;
using Xunit;

namespace PlantOps.Tests;

public sealed class DatabaseFoundationTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task InitializerRejectsNonDevelopmentBeforeConnecting(string environment)
    {
        await using var db = new PlantOpsDbContext(new DbContextOptionsBuilder<PlantOpsDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=59999;Database=unused").Options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DevelopmentDatabaseInitializer.InitializeAsync(db, environment));
    }
    [DatabaseFact]
    public async Task InitializationIsRepeatableWithoutOverwritingExistingSeed()
    {
        await using var db = CreateContext();
        await DevelopmentDatabaseInitializer.InitializeAsync(db, "Development");
        var asset = await db.Assets.SingleAsync(x => x.Code == "P-101");
        var existingName = asset.Name;
        asset.Name = "Preserved development edit";
        await db.SaveChangesAsync();
        try
        {
            await DevelopmentDatabaseInitializer.InitializeAsync(db, "Development");
            Assert.Equal(1, await db.Plants.CountAsync(x => x.Code == "NORTH"));
            Assert.Equal(1, await db.Locations.CountAsync(x => x.Code == "UTILITIES"));
            Assert.Equal(1, await db.Assets.CountAsync(x => x.Code == "P-101"));
            await db.Entry(asset).ReloadAsync();
            Assert.Equal("Preserved development edit", asset.Name);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            asset.Name = existingName;
            await db.SaveChangesAsync();
        }
    }

    [DatabaseFact]
    public async Task DatabaseRejectsAnAssetLinkedToAnotherPlantsLocation()
    {
        await using var db = CreateContext();
        await DevelopmentDatabaseInitializer.InitializeAsync(db, "Development");
        await using var transaction = await db.Database.BeginTransactionAsync();
        var otherPlant = new Plant { Id = Guid.NewGuid(), Code = Guid.NewGuid().ToString("N"), Name = "Other test plant" };
        db.Plants.Add(otherPlant);
        await db.SaveChangesAsync();
        var location = await db.Locations.SingleAsync(x => x.Code == "UTILITIES");
        db.Assets.Add(new Asset { Id = Guid.NewGuid(), PlantId = otherPlant.Id, LocationId = location.Id,
            Code = "INVALID", Name = "Cross-plant asset", AssetType = "Pump" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private static PlantOpsDbContext CreateContext() => new(new DbContextOptionsBuilder<PlantOpsDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("PLANTOPS_TEST_CONNECTION")).Options);
}

public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PLANTOPS_TEST_CONNECTION")))
            Skip = "Set PLANTOPS_TEST_CONNECTION to a dedicated PostgreSQL test database.";
    }
}


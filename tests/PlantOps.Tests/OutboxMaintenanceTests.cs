using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PlantOps.Domain.Operations;
using PlantOps.Infrastructure.Operations;
using PlantOps.Infrastructure.Persistence;
using Xunit;

namespace PlantOps.Tests;

[Collection("Database")]
public sealed class OutboxMaintenanceTests
{
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    }

    private static IConfiguration Config(bool enabled, int batch = 1) => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["Outbox:RetentionEnabled"] = enabled.ToString(), ["Outbox:CleanupBatchSize"] = batch.ToString() }).Build();

    private static PlantOpsDbContext Db(string connection, AssetManagementTests.FailAfterSave? failure = null)
    {
        var options = new DbContextOptionsBuilder<PlantOpsDbContext>().UseNpgsql(connection);
        if (failure != null)
        {
            options.AddInterceptors(failure);
        }

        return new(options.Options);
    }

    private static OutboxEvent Row(Clock clock, double? sentHours) => new()
    {
        PlantId = Guid.NewGuid(),
        EntityId = Guid.NewGuid(),
        EventType = "WorkOrderChanged",
        OccurredAt = clock.GetUtcNow().AddDays(-10),
        DispatchedAt = sentHours.HasValue ? clock.GetUtcNow().AddHours(-sentHours.Value) : null,
        AvailableAt = clock.GetUtcNow(),
        Attempts = 10
    };

    [DatabaseFact]
    public async Task ExplicitOptInBoundedCleanupPreservesPendingRecentAndBoundary()
    {
        var connection = await TestDatabase.IsolatedConnection();
        var clock = new Clock();
        await using var db = Db(connection);
        await DevelopmentDatabaseInitializer.InitializeAsync(db, "Development");
        var oldA = Row(clock, 48);
        var oldB = Row(clock, 49);
        var pending = Row(clock, null);
        var recent = Row(clock, 1);
        var boundary = Row(clock, 24);
        db.OutboxEvents.AddRange(oldA, oldB, pending, recent, boundary);
        await db.SaveChangesAsync();
        Assert.Equal(0, await new OutboxMaintenance(db, clock, Config(false)).RetainAsync());
        Assert.Equal(1, await new OutboxMaintenance(db, clock, Config(true)).RetainAsync());
        Assert.Equal(1, await new OutboxMaintenance(db, clock, Config(true)).RetainAsync());
        Assert.Equal(0, await new OutboxMaintenance(db, clock, Config(true)).RetainAsync());
        Assert.True(await db.OutboxEvents.AnyAsync(x => x.Id == pending.Id));
        Assert.True(await db.OutboxEvents.AnyAsync(x => x.Id == recent.Id));
        Assert.True(await db.OutboxEvents.AnyAsync(x => x.Id == boundary.Id));
    }

    [DatabaseFact]
    public async Task LockedSentRowIsSkippedAndFailureRollsBackDeletion()
    {
        var connection = await TestDatabase.IsolatedConnection();
        var clock = new Clock();
        await using var db = Db(connection);
        await DevelopmentDatabaseInitializer.InitializeAsync(db, "Development");
        var row = Row(clock, 48);
        db.OutboxEvents.Add(row);
        await db.SaveChangesAsync();
        await using (var locker = Db(connection))
        {
            await using var tx = await locker.Database.BeginTransactionAsync();
            await locker.OutboxEvents.FromSqlInterpolated($"SELECT * FROM \"OutboxEvents\" WHERE \"Id\" = {row.Id} FOR UPDATE").ToListAsync();
            Assert.Equal(0, await new OutboxMaintenance(db, clock, Config(true)).RetainAsync());
        }
        var failure = new AssetManagementTests.FailAfterSave { Enabled = true };
        await using var failing = Db(connection, failure);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OutboxMaintenance(failing, clock, Config(true)).RetainAsync());
        Assert.True(await db.OutboxEvents.AnyAsync(x => x.Id == row.Id));
        Assert.Equal(1, await new OutboxMaintenance(db, clock, Config(true)).RetainAsync());
    }
}

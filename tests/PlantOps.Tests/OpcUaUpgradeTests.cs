using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PlantOps.Application.Telemetry;
using PlantOps.Domain.Assets;
using PlantOps.Domain.Knowledge;
using PlantOps.Domain.Maintenance;
using PlantOps.Domain.Telemetry;
using PlantOps.Infrastructure.Persistence;
using Xunit;

namespace PlantOps.Tests;

[Collection("Database")]
public sealed class OpcUaUpgradeTests
{
    [DatabaseFact]
    public async Task NonEmptyP1SchemaUpgradesWithoutChangingReadingsBusinessRecordsOrFiles()
    {
        var schema = "p2_upgrade_" + Guid.NewGuid().ToString("N");
        await using var control = new PlantOpsDbContext(new DbContextOptionsBuilder<PlantOpsDbContext>().UseNpgsql(TestDatabase.Connection).Options);
        // Schema name consists exclusively of a fixed prefix and generated hexadecimal GUID.
        var createSchema = $"CREATE SCHEMA \"{schema}\"";
        await control.Database.ExecuteSqlRawAsync(createSchema);
        await using var db = new PlantOpsDbContext(new DbContextOptionsBuilder<PlantOpsDbContext>()
            .UseNpgsql(TestDatabase.Connection + ";Search Path=" + schema).Options);
        await db.GetService<IMigrator>().MigrateAsync("20261006002351_ReviewIngestionAndOutboxSafety");
        var plant = new Plant { Id = Guid.NewGuid(), Code = "P2-UPGRADE", Name = "Preserved plant" };
        var location = new Location { Id = Guid.NewGuid(), PlantId = plant.Id, Code = "AREA", Name = "Preserved location" };
        var asset = new Asset { Id = Guid.NewGuid(), PlantId = plant.Id, LocationId = location.Id, Code = "PUMP", Name = "Preserved asset" };
        db.AddRange(plant, location, asset);
        await db.SaveChangesAsync();
        var point = new MeasurementPoint { Id = Guid.NewGuid(), PlantId = plant.Id, AssetId = asset.Id, Code = "VIB", Name = "Vibration" };
        var source = new DataSource { Id = Guid.NewGuid(), PlantId = plant.Id, Name = "Preserved Simulation" };
        db.AddRange(point, source);
        await db.SaveChangesAsync();
        var mapping = new SourceMapping { Id = Guid.NewGuid(), PlantId = plant.Id, PointId = point.Id, DataSourceId = source.Id, SourceKey = "vibration" };
        db.SourceMappings.Add(mapping);
        await db.SaveChangesAsync();
        var at = new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE \"MeasurementReadings_20261006\" PARTITION OF \"MeasurementReadings\" FOR VALUES FROM ('2026-10-06') TO ('2026-10-07')");
        var config = new SourceConfiguration(plant.Id, asset.Id, point.Id, 1, "mm/s", 1000, 5000,
            source.Id, 1, "Simulation", mapping.Id, 1, "vibration", "mm/s", 1, 0, mapping.SourceParameters, source.ConnectionParameters);
        var reading = SamplePolicy.ToReading(SamplePolicy.Normalize(config, 4, "Good", at, null, at, "preserved-event"));
        var document = new Document { PlantId = plant.Id, AssetId = asset.Id, Title = "Original validation document" };
        var workOrder = new WorkOrder { PlantId = plant.Id, AssetId = asset.Id, Number = "P2-PRESERVE", Title = "Original work order", CreatedByUserId = "test", CreatedAt = at };
        db.AddRange(reading, document, workOrder);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "MeasurementMinutes" ("PointId","BucketStart","Min","Max","Average","GoodCount","UncertainCount","BadCount","AnomalyCount","SampleCount","HasGap","CoverageRatio")
            VALUES ({point.Id},{at},4,4,4,1,0,0,0,1,false,1)
            """);
        await db.Entry(reading).ReloadAsync();
        var beforeReading = JsonSerializer.Serialize(reading);
        var beforeDocument = JsonSerializer.Serialize(document);
        var beforeOrder = JsonSerializer.Serialize(workOrder);
        var beforeOutbox = await db.OutboxEvents.CountAsync();
        var root = Environment.GetEnvironmentVariable("PLANTOPS_TEST_DOCUMENT_ROOT") ?? Path.GetFullPath("artifacts/p2/documents/upgrade");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, schema + ".md");
        await File.WriteAllTextAsync(file, "# Original isolated migration validation document\n");
        var beforeFile = await File.ReadAllBytesAsync(file);
        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(beforeReading, JsonSerializer.Serialize(await db.MeasurementReadings.SingleAsync()));
        Assert.Equal(beforeDocument, JsonSerializer.Serialize(await db.Documents.SingleAsync()));
        Assert.Equal(beforeOrder, JsonSerializer.Serialize(await db.WorkOrders.SingleAsync()));
        Assert.Equal(beforeOutbox, await db.OutboxEvents.CountAsync());
        Assert.Equal(beforeFile, await File.ReadAllBytesAsync(file));
        var minute = await db.MeasurementMinutes.SingleAsync();
        Assert.Equal(1.0, minute.CoverageRatio);
        Assert.Equal("Periodic", minute.SamplingMode);
        Assert.Empty(await db.SamplingProfiles.ToListAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        await File.WriteAllTextAsync(Path.Combine(root, schema + ".json"), JsonSerializer.Serialize(new
        {
            Schema = schema,
            ReadingPreserved = true,
            DocumentPreserved = true,
            WorkOrderPreserved = true,
            OutboxBefore = beforeOutbox,
            OutboxAfter = await db.OutboxEvents.CountAsync(),
            FilePreserved = true,
            PendingMigrations = 0,
            LegacyCoverage = minute.CoverageRatio
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

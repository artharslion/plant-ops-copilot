using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PlantOps.Application.Telemetry;
using PlantOps.Infrastructure.Persistence;
using PlantOps.Infrastructure.Telemetry;
using Xunit;

namespace PlantOps.Tests;

[Collection("Database")]
public sealed class MeasurementMaintenanceTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Clock Clock { get; } = new();

        public PlantOpsDbContext Db
        {
            get;
        }

        public SourceConfiguration Source { get; private set; } = null!;

        public AssetManagementTests.FailAfterSave Failure { get; } = new();

        public MeasurementMaintenance Maintenance => new(Db, Clock, new ConfigurationBuilder().Build());

        private Fixture(string connection)
        {
            Db = new(new DbContextOptionsBuilder<PlantOpsDbContext>().UseNpgsql(connection).AddInterceptors(Failure).Options);
        }

        public static async Task<Fixture> Create()
        {
            var schema = "p17_test_" + Guid.NewGuid().ToString("N");
            await using var db = new PlantOpsDbContext(new DbContextOptionsBuilder<PlantOpsDbContext>().UseNpgsql(TestDatabase.Connection).Options);
            var sql = $"CREATE SCHEMA \"{schema}\"";
            await db.Database.ExecuteSqlRawAsync(sql);
            var f = new Fixture(TestDatabase.Connection + ";Search Path=" + schema);
            await DevelopmentDatabaseInitializer.InitializeAsync(f.Db, "Development");
            f.Db.ChangeTracker.Clear();
            f.Source = Assert.Single(await new SourceConfigurationReader(f.Db).GetEnabledAsync());
            return f;
        }

        public async Task Send(DateTimeOffset at, double value = 12.6, string quality = "Good", string? key = null)
        {
            await new MeasurementIngestor(Db, Clock).IngestAsync([SamplePolicy.Normalize(Source, value, quality, at, null, at, key ?? at.UtcTicks.ToString())]);
            Db.ChangeTracker.Clear();
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [DatabaseFact]
    public async Task ExplicitLossResetsPendingIsIdempotentAndIsVisibleWithCompleteMinuteSamples()
    {
        await using var f = await Fixture.Create();
        var start = f.Clock.Now;
        // Every second is present, so sample counts alone cannot reveal this uncertain interval.
        for (var i = 0; i < 60; i++)
        {
            f.Clock.Now = start.AddSeconds(i);
            await f.Send(start.AddSeconds(i), 4);
        }
        f.Clock.Now = start.AddMinutes(2);
        await f.Maintenance.AggregateAsync();
        Assert.False((await f.Db.MeasurementMinutes.SingleAsync()).HasGap);
        var rule = new PlantOps.Domain.Alarms.AlarmRule { Id = Guid.NewGuid(), PlantId = f.Source.PlantId, PointId = f.Source.PointId, Name = "Continuity test" };
        f.Db.AlarmRules.Add(rule);
        var runtime = new PlantOps.Domain.Alarms.RuleRuntimeState { RuleId = rule.Id, PendingRaiseSince = start, LastEvaluatedAt = start.AddSeconds(30), LastInputTrusted = true };
        f.Db.RuleRuntimeStates.Add(runtime);
        await f.Db.SaveChangesAsync();
        var loss = new IngestionLoss(Guid.NewGuid(), f.Source, start.AddSeconds(20), start.AddSeconds(21), "QueueOverflow", 2);
        var recorder = new IngestionGapRecorder(f.Db, f.Clock);
        await recorder.RecordAsync([loss]);
        Assert.Null(runtime.PendingRaiseSince);
        var version = runtime.Version;
        // A retried summary must not reset a newly started duration or add its count twice.
        runtime.PendingRaiseSince = start.AddSeconds(40);
        await f.Db.SaveChangesAsync();
        await recorder.RecordAsync([loss]);
        Assert.Equal(start.AddSeconds(40), runtime.PendingRaiseSince);
        Assert.Equal(version, runtime.Version);
        var gap = Assert.Single(await f.Db.IngestionGaps.ToListAsync());
        Assert.Equal(2, gap.DroppedCount);
        await f.Db.Database.ExecuteSqlRawAsync("INSERT INTO \"DirtyMeasurementMinutes\" SELECT \"PointId\", \"BucketStart\" FROM \"MeasurementMinutes\" ON CONFLICT DO NOTHING");
        await f.Maintenance.AggregateAsync();
        f.Db.ChangeTracker.Clear();
        Assert.True((await f.Db.MeasurementMinutes.SingleAsync()).HasGap);
        var point = await f.Db.MeasurementPoints.SingleAsync();
        var readings = await f.Db.MeasurementReadings.ToListAsync();
        var trend = TrendBuilder.Build(point, readings, start, start.AddMinutes(1), "raw", f.Clock.Now, [gap]);
        Assert.True(trend.Samples.Single(x => x.EffectiveAt == start.AddSeconds(20)).GapBefore);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.RecordAsync([loss with { Id = Guid.NewGuid(), Configuration = f.Source with { PlantId = Guid.NewGuid() } }]));
    }

    [DatabaseFact]
    public async Task OpenFailureIsRecordedBeforeRecoveryAndRecoveryDoesNotReopenOnRetry()
    {
        await using var f = await Fixture.Create();
        var at = f.Clock.Now;
        var loss = new IngestionLoss(Guid.NewGuid(), f.Source, at, null, "CollectionFailure", null, true);
        var recorder = new IngestionGapRecorder(f.Db, f.Clock);
        await recorder.RecordAsync([loss]);
        var gap = Assert.Single(await f.Db.IngestionGaps.ToListAsync());
        Assert.Null(gap.To);
        Assert.True(gap.IsPossible);
        await recorder.RecoverAsync([f.Source], at.AddSeconds(10));
        f.Db.ChangeTracker.Clear();
        await recorder.RecordAsync([loss]);
        gap = Assert.Single(await f.Db.IngestionGaps.ToListAsync());
        Assert.Equal(at.AddSeconds(10), gap.To);
        Assert.Equal(at.AddSeconds(10), gap.RecoveredAt);
    }

    [DatabaseFact]
    public async Task StartupWatermarkCreatesStablePossibleTailWithoutInventingLostCounts()
    {
        await using var f = await Fixture.Create();
        var at = f.Clock.Now;
        await f.Send(at, 4);
        f.Clock.Now = at.AddSeconds(20);
        var recorder = new IngestionGapRecorder(f.Db, f.Clock);
        await recorder.DetectStartupAsync([f.Source]);
        await recorder.DetectStartupAsync([f.Source]);
        var gap = Assert.Single(await f.Db.IngestionGaps.ToListAsync());
        Assert.Equal("PossibleCrashTail", gap.Reason);
        Assert.True(gap.IsPossible);
        Assert.Null(gap.DroppedCount);
        Assert.Null(gap.To);
        await f.Send(f.Clock.Now, 4);
        gap = Assert.Single(await f.Db.IngestionGaps.Where(x => x.Reason == "PossibleCrashTail").ToListAsync());
        Assert.Equal(f.Clock.Now, gap.To);
    }

    [DatabaseFact]
    public async Task DistantFutureCannotCreateArbitraryPartitions()
    {
        await using var f = await Fixture.Create();
        await Assert.ThrowsAsync<ArgumentException>(() => new MeasurementIngestor(f.Db, f.Clock).IngestAsync([
            SamplePolicy.Normalize(f.Source, 4, "Good", f.Clock.Now.AddYears(10), null, f.Clock.Now, "distant-future")]));
        Assert.Empty(await f.Db.MeasurementPartitions.ToListAsync());
    }

    [DatabaseFact]
    public async Task DeduplicationUsesUtcDateAndSameDayRetriesAreIdempotent()
    {
        await using var f = await Fixture.Create();
        var midnight = f.Clock.Now;
        await f.Send(midnight.AddSeconds(-1), 4, key: "shared-key");
        await f.Send(midnight, 6, key: "shared-key");
        await f.Send(midnight, 6, key: "shared-key");
        Assert.Equal(2, await f.Db.MeasurementReadings.CountAsync());
        Assert.Equal(2, await f.Db.MeasurementPartitions.CountAsync());
        Assert.Equal(6, (await f.Db.PointLatestStates.SingleAsync()).LatestObservedValue);
    }

    [DatabaseFact]
    public async Task UtcPartitionsPersistentMinutesLateValuesQualityAndDedup()
    {
        await using var f = await Fixture.Create();
        var at = f.Clock.Now.AddSeconds(-1);
        await f.Send(at, 4);
        await f.Send(at.AddSeconds(1), 10, "Bad");
        await f.Send(at.AddSeconds(2), 6, "Uncertain");
        Assert.Equal(2, await f.Db.MeasurementPartitions.CountAsync());
        Assert.Equal(3, await f.Db.MeasurementReadings.CountAsync());
        f.Clock.Now = at.AddMinutes(2);
        Assert.Equal(2, await f.Maintenance.AggregateAsync());
        var minutes = await f.Db.MeasurementMinutes.OrderBy(x => x.BucketStart).ToListAsync();
        Assert.Equal(4, minutes[0].Average);
        Assert.Equal(1, minutes[1].BadCount);
        Assert.Equal(1, minutes[1].UncertainCount);
        Assert.Null(minutes[1].Average);
        Assert.All(minutes, x => Assert.True(x.HasGap));
        Assert.Equal(0, await f.Maintenance.AggregateAsync());
        await f.Send(at.AddSeconds(-1), 8, key: "late");
        Assert.Single(await f.Db.DirtyMeasurementMinutes.ToListAsync());
        await f.Maintenance.AggregateAsync();
        f.Db.ChangeTracker.Clear();
        Assert.Equal(6, (await f.Db.MeasurementMinutes.OrderBy(x => x.BucketStart).FirstAsync()).Average);
        await f.Send(at.AddSeconds(-1), 8, key: "late");
        Assert.Equal(4, await f.Db.MeasurementReadings.CountAsync());
        Assert.Empty(await f.Db.DirtyMeasurementMinutes.ToListAsync());
        Assert.False(f.Db.Database.HasPendingModelChanges());
    }

    [DatabaseFact]
    public async Task RetentionBlocksDirtyPartitionsPreservesBusinessSnapshotsAndExpiresMinutesLater()
    {
        await using var f = await Fixture.Create();
        var now = f.Clock.Now;
        var old = now.AddDays(-8);
        f.Clock.Now = old;
        for (var i = 0; i < 4; i++)
        {
            f.Clock.Now = old.AddSeconds(i);
            await f.Send(old.AddSeconds(i));
        }
        Assert.Single(await f.Db.Alarms.ToListAsync());
        f.Clock.Now = now;
        var blocked = await f.Maintenance.RetainAsync();
        Assert.Equal(1, blocked.BlockedPartitions);
        Assert.Equal(0, blocked.DroppedPartitions);
        Assert.Equal(4, await f.Db.MeasurementReadings.CountAsync());
        await f.Maintenance.AggregateAsync();
        var retained = await f.Maintenance.RetainAsync();
        Assert.Equal(1, retained.DroppedPartitions);
        Assert.Empty(await f.Db.MeasurementReadings.ToListAsync());
        Assert.Single(await f.Db.Alarms.ToListAsync());
        Assert.Single(await f.Db.PointLatestStates.ToListAsync());
        Assert.Single(await f.Db.MeasurementMinutes.ToListAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => f.Send(old.AddSeconds(30), 999, key: "expired-late"));
        f.Db.ChangeTracker.Clear();
        Assert.Empty(await f.Db.MeasurementPartitions.ToListAsync());
        Assert.Equal(4, (await f.Db.MeasurementMinutes.SingleAsync()).SampleCount);
        var extended = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Telemetry:RawRetentionDays"] = "30" }).Build();
        await Assert.ThrowsAsync<ArgumentException>(() => new MeasurementIngestor(f.Db, f.Clock, extended).IngestAsync([SamplePolicy.Normalize(f.Source, 999, "Good", old, null, old, "extended-retention")]));
        Assert.Equal(0, (await f.Maintenance.RetainAsync()).DroppedPartitions);
        f.Clock.Now = now.AddDays(100);
        Assert.Equal(1, (await f.Maintenance.RetainAsync()).DeletedMinutes);
    }

    [DatabaseFact]
    public async Task RetentionSqlFailureRollsBackPartitionDropAndAudit()
    {
        await using var f = await Fixture.Create();
        var now = f.Clock.Now;
        var old = now.AddDays(-8);
        f.Clock.Now = old;
        await f.Send(old, 4);
        f.Clock.Now = now;
        await f.Maintenance.AggregateAsync();
        f.Failure.Enabled = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Maintenance.RetainAsync());
        f.Failure.Enabled = false;
        f.Db.ChangeTracker.Clear();
        Assert.Single(await f.Db.MeasurementReadings.ToListAsync());
        Assert.Single(await f.Db.MeasurementPartitions.ToListAsync());
        Assert.Empty(await f.Db.AuditLogs.Where(x => x.Action == "DropPartition").ToListAsync());
        Assert.Equal(1, (await f.Maintenance.RetainAsync()).DroppedPartitions);
    }

    [DatabaseFact]
    public async Task SamplingInterruptionsPersistAndMinuteCoverageDoesNotHideMissingTime()
    {
        await using var f = await Fixture.Create();
        var start = f.Clock.Now;
        await f.Send(start, 4);
        await f.Send(start.AddSeconds(20), 4);
        Assert.Single(await f.Db.IngestionGaps.ToListAsync());
        f.Clock.Now = start.AddMinutes(2);
        await f.Maintenance.AggregateAsync();
        var minute = Assert.Single(await f.Db.MeasurementMinutes.ToListAsync());
        Assert.True(minute.HasGap);
        Assert.Equal(2.0 / 60, minute.CoverageRatio, 8);
    }
}

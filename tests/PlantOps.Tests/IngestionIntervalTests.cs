using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlantOps.Application.Telemetry;
using PlantOps.Domain.Telemetry;
using PlantOps.Infrastructure.Persistence;
using PlantOps.Infrastructure.Telemetry;
using Xunit;

namespace PlantOps.Tests;

[Collection("Database")]
public sealed class IngestionIntervalTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 9, 0, 1, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Schema { get; } = "p2_ingest_" + Guid.NewGuid().ToString("N");

        public Clock Clock { get; } = new();

        public DateTimeOffset Start => Clock.Now.AddMinutes(-1);

        public PlantOpsDbContext Db { get; private set; } = null!;

        public SourceConfiguration Source { get; private set; } = null!;

        public AssetManagementTests.FailAfterSave Failure { get; } = new();

        public static async Task<Fixture> Create(bool simulation = false)
        {
            var f = new Fixture();
            await using var root = new PlantOpsDbContext(new DbContextOptionsBuilder<PlantOpsDbContext>().UseNpgsql(TestDatabase.Connection).Options);
            var sql = $"CREATE SCHEMA \"{f.Schema}\"";
            await root.Database.ExecuteSqlRawAsync(sql);
            f.Db = new(new DbContextOptionsBuilder<PlantOpsDbContext>().UseNpgsql(TestDatabase.Connection + ";Search Path=" + f.Schema)
                .AddInterceptors(f.Failure).Options);
            await DevelopmentDatabaseInitializer.InitializeAsync(f.Db, "Development");
            var source = await f.Db.DataSources.SingleAsync();
            if (!simulation)
            {
                source.Kind = "OpcUa";
            }
            var point = await f.Db.MeasurementPoints.SingleAsync();
            point.StaleAfterMs = 60000;
            var rule = await f.Db.AlarmRules.SingleAsync();
            rule.MaxSampleGapMs = 60000;
            rule.RaiseForMs = 30000;
            rule.ClearForMs = 30000;
            await f.Db.SaveChangesAsync();
            f.Db.ChangeTracker.Clear();
            f.Source = Assert.Single(await new SourceConfigurationReader(f.Db).GetEnabledAsync());
            return f;
        }

        public SamplingProfile Profile(int from, int? to, double interval = 1000, string mode = "Periodic",
            Guid? mapping = null, long? version = null) => new()
            {
                PointId = Source.PointId,
                SourceMappingId = mapping ?? Source.MappingId,
                MappingVersion = version ?? Source.MappingVersion,
                EffectiveFrom = Start.AddSeconds(from),
                EffectiveTo = to.HasValue ? Start.AddSeconds(to.Value) : null,
                Mode = mode,
                RevisedSamplingIntervalMs = mode == "Periodic" ? interval : null
            };

        public async Task History(params SamplingProfile[] profiles)
        {
            Db.SamplingProfiles.AddRange(profiles);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public NormalizedSample Sample(int second, double value = 12.6, string? key = null) =>
            SamplePolicy.Normalize(Source, value, "Good", Start.AddSeconds(second), null, Clock.Now, key ?? second.ToString());

        public async Task<IngestResult> Send(params NormalizedSample[] samples)
        {
            var result = await new MeasurementIngestor(Db, Clock).IngestAsync(samples);
            Db.ChangeTracker.Clear();
            return result;
        }

        public async Task<List<IngestionGap>> Gaps() => await Db.IngestionGaps.AsNoTracking().OrderBy(x => x.From).ToListAsync();

        public void AssertPossible(IngestionGap gap, int from, int to)
        {
            Assert.Equal(Start.AddSeconds(from), gap.From);
            Assert.Equal(Start.AddSeconds(to), gap.To);
            Assert.Equal(Source.PointId, gap.PointId);
            Assert.Equal(Source.DataSourceId, gap.DataSourceId);
            Assert.Equal("PossibleSamplingIntervalGap", gap.Reason);
            Assert.True(gap.IsPossible);
            Assert.Null(gap.DroppedCount);
            Assert.Equal(Clock.Now, gap.DetectedAt);
        }

        public async ValueTask DisposeAsync()
        {
            var evidence = Environment.GetEnvironmentVariable("PLANTOPS_P2_INGEST_EVIDENCE_ROOT");
            if (!string.IsNullOrWhiteSpace(evidence))
            {
                Directory.CreateDirectory(evidence);
                var snapshot = new
                {
                    Schema,
                    Profiles = await Db.SamplingProfiles.AsNoTracking().ToListAsync(),
                    Gaps = await Gaps(),
                    Readings = await Db.MeasurementReadings.AsNoTracking().OrderBy(x => x.EffectiveAt).ToListAsync(),
                    Rules = await Db.RuleRuntimeStates.AsNoTracking().ToListAsync(),
                    Alarms = await Db.Alarms.AsNoTracking().ToListAsync()
                };
                await File.WriteAllTextAsync(Path.Combine(evidence, Schema + ".json"), JsonSerializer.Serialize(snapshot));
            }
            await Db.DisposeAsync();
        }
    }

    [DatabaseFact]
    public async Task SlowRevisionCannotHideEarlierFastSegment()
    {
        await using var f = await Fixture.Create();
        await f.History(f.Profile(0, 30), f.Profile(30, null, 10000));
        await f.Send(f.Sample(20), f.Sample(35));
        f.AssertPossible(Assert.Single(await f.Gaps()), 20, 30);
    }

    [DatabaseFact]
    public async Task FastRevisionDoesNotExtendEvidenceIntoSlowSegment()
    {
        await using var f = await Fixture.Create();
        await f.History(f.Profile(0, 30, 10000), f.Profile(30, null));
        await f.Send(f.Sample(29), f.Sample(40));
        f.AssertPossible(Assert.Single(await f.Gaps()), 30, 40);
    }

    [DatabaseFact]
    public async Task ShortFastSegmentAndSlowToleranceDoNotInventLoss()
    {
        await using var f = await Fixture.Create();
        await f.History(f.Profile(0, 30, 10000), f.Profile(30, null));
        await f.Send(f.Sample(20), f.Sample(31));
        Assert.Empty(await f.Gaps());
    }

    [DatabaseFact]
    public async Task MultipleRevisionsPersistSeparateWholeEvidenceSegments()
    {
        await using var f = await Fixture.Create();
        await f.History(f.Profile(0, 10), f.Profile(10, 20, 2000), f.Profile(20, null));
        await f.Send(f.Sample(0), f.Sample(25));
        var gaps = await f.Gaps();
        Assert.Equal(3, gaps.Count);
        f.AssertPossible(gaps[0], 0, 10);
        f.AssertPossible(gaps[1], 10, 20);
        f.AssertPossible(gaps[2], 20, 25);
    }

    [DatabaseFact]
    public async Task ChangesUnknownAndMissingHistoryLeaveKnownPeriodicEvidence()
    {
        await using var f = await Fixture.Create();
        await f.History(f.Profile(0, 5), f.Profile(5, 10, mode: "Changes"),
            f.Profile(10, 15, mode: "Unknown"), f.Profile(20, 25));
        await f.Send(f.Sample(0), f.Sample(30));
        var gaps = await f.Gaps();
        Assert.Equal(2, gaps.Count);
        f.AssertPossible(gaps[0], 0, 5);
        f.AssertPossible(gaps[1], 20, 25);
    }

    [DatabaseFact]
    public async Task OtherMappingAndVersionEvidenceIsNotAttributedToCurrentSource()
    {
        await using var f = await Fixture.Create();
        await f.History(f.Profile(0, 5, mapping: Guid.NewGuid()), f.Profile(5, 10, version: f.Source.MappingVersion + 1),
            f.Profile(10, 15));
        await f.Send(f.Sample(0), f.Sample(20));
        f.AssertPossible(Assert.Single(await f.Gaps()), 10, 15);
    }

    [DatabaseFact]
    public async Task ConflictingMappingHistoryRemainsUnknownBeforeIdentityFiltering()
    {
        await using var f = await Fixture.Create();
        await f.History(f.Profile(0, 20), f.Profile(5, 15, mapping: Guid.NewGuid()));
        await f.Send(f.Sample(0), f.Sample(20));
        var gaps = await f.Gaps();
        Assert.Equal(2, gaps.Count);
        f.AssertPossible(gaps[0], 0, 5);
        f.AssertPossible(gaps[1], 15, 20);
    }

    [DatabaseFact]
    public async Task InferredGapResetsPendingWithinSameBatchWithoutRepeatedReset()
    {
        await using var f = await Fixture.Create();
        await f.History(f.Profile(0, 30), f.Profile(30, null, 10000));
        await f.Send(f.Sample(20), f.Sample(35), f.Sample(40));
        Assert.Empty(await f.Db.Alarms.ToListAsync());
        Assert.Equal(f.Start.AddSeconds(35), (await f.Db.RuleRuntimeStates.SingleAsync()).PendingRaiseSince);
        f.AssertPossible(Assert.Single(await f.Gaps()), 20, 30);
    }

    [DatabaseFact]
    public async Task LateAndDuplicateSamplesDoNotRewriteGapsOrResetNewerPending()
    {
        await using var f = await Fixture.Create();
        await f.History(f.Profile(0, null));
        await f.Send(f.Sample(0), f.Sample(4), f.Sample(5));
        var before = (await f.Db.RuleRuntimeStates.AsNoTracking().SingleAsync()).PendingRaiseSince;
        var gapIds = (await f.Gaps()).Select(x => x.Id).ToArray();
        Assert.Equal(1, (await f.Send(f.Sample(4))).Duplicates);
        Assert.Equal(1, (await f.Send(f.Sample(2, key: "late"))).Inserted);
        Assert.Equal(before, (await f.Db.RuleRuntimeStates.AsNoTracking().SingleAsync()).PendingRaiseSince);
        Assert.Equal(gapIds, (await f.Gaps()).Select(x => x.Id).ToArray());
        Assert.Equal(f.Start.AddSeconds(5), (await f.Db.PointLatestStates.SingleAsync()).LatestObservedEffectiveAt);
    }

    [DatabaseFact]
    public async Task ExactToleranceAndEqualObservationTimesDoNotCreateGaps()
    {
        await using var f = await Fixture.Create();
        await f.History(f.Profile(0, null, 2000));
        await f.Send(f.Sample(0), f.Sample(3), f.Sample(3, key: "same-time"));
        Assert.Empty(await f.Gaps());
        await f.Send(f.Sample(7));
        f.AssertPossible(Assert.Single(await f.Gaps()), 3, 7);
    }

    [DatabaseFact]
    public async Task GapReadingsAndRulesRollbackTogetherAndRetryOnce()
    {
        await using var f = await Fixture.Create();
        await f.History(f.Profile(0, 30), f.Profile(30, null, 10000));
        await f.Send(f.Sample(20));
        f.Failure.Enabled = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Send(f.Sample(35)));
        f.Failure.Enabled = false;
        f.Db.ChangeTracker.Clear();
        Assert.Empty(await f.Gaps());
        Assert.Single(await f.Db.MeasurementReadings.ToListAsync());
        Assert.Equal(f.Start.AddSeconds(20), (await f.Db.RuleRuntimeStates.SingleAsync()).PendingRaiseSince);
        await f.Send(f.Sample(35));
        f.AssertPossible(Assert.Single(await f.Gaps()), 20, 30);
    }

    [DatabaseFact]
    public async Task SimulationKeepsFixedRangeAndResetsSameBatchPending()
    {
        await using var f = await Fixture.Create(simulation: true);
        await f.Send(f.Sample(0), f.Sample(4), f.Sample(5));
        var gap = Assert.Single(await f.Gaps());
        Assert.Equal(f.Start.AddMilliseconds(f.Source.ExpectedIntervalMs), gap.From);
        Assert.Equal(f.Start.AddSeconds(4), gap.To);
        Assert.Equal("SampleIntervalGap", gap.Reason);
        Assert.False(gap.IsPossible);
        Assert.Equal(f.Start.AddSeconds(4), (await f.Db.RuleRuntimeStates.SingleAsync()).PendingRaiseSince);
    }

    [DatabaseFact]
    public async Task InferredGapResetsClearDurationAndLaterContinuousSamplesRecover()
    {
        await using var f = await Fixture.Create();
        var rule = await f.Db.AlarmRules.SingleAsync();
        rule.RaiseForMs = 0;
        await f.Db.SaveChangesAsync();
        await f.History(f.Profile(0, 30), f.Profile(30, null, 10000));
        await f.Send(f.Sample(0));
        Assert.Equal("Active", (await f.Db.Alarms.SingleAsync()).State);
        // A fast segment gap resets the low-value duration even though the
        // current slow interval alone would not report it.
        await f.Send(f.Sample(20, 4), f.Sample(35, 4), f.Sample(40, 4), f.Sample(50, 4), f.Sample(60, 4));
        Assert.Equal("Active", (await f.Db.Alarms.SingleAsync()).State);
        Assert.Equal(f.Start.AddSeconds(35), (await f.Db.RuleRuntimeStates.SingleAsync()).PendingClearSince);
        await f.Send(f.Sample(65, 4));
        Assert.Equal("Recovered", (await f.Db.Alarms.SingleAsync()).State);
        Assert.Equal(2, await f.Db.AlarmEvents.CountAsync());
    }

    [DatabaseFact]
    public async Task FirstObservationAndWhollyUnknownOrChangesHistoryDoNotInferLoss()
    {
        foreach (var mode in new[] { "Changes", "Unknown", "Missing" })
        {
            await using var f = await Fixture.Create();
            if (mode != "Missing")
            {
                await f.History(f.Profile(0, null, mode: mode));
            }
            await f.Send(f.Sample(20));
            Assert.Empty(await f.Gaps());
            await f.Send(f.Sample(50));
            Assert.Empty(await f.Gaps());
        }
    }

    [DatabaseFact]
    public async Task TimeAnomalyIsArchivedWithoutGapOrObservedWatermarkAdvance()
    {
        await using var f = await Fixture.Create();
        await f.History(f.Profile(0, null));
        await f.Send(f.Sample(0), f.Sample(80), f.Sample(1));
        Assert.Empty(await f.Gaps());
        Assert.Equal(3, await f.Db.MeasurementReadings.CountAsync());
        Assert.Single(await f.Db.MeasurementReadings.Where(x => x.TimeAnomaly).ToListAsync());
        Assert.Equal(f.Start.AddSeconds(1), (await f.Db.PointLatestStates.SingleAsync()).LatestObservedEffectiveAt);
    }

    [DatabaseFact]
    public async Task LateSampleWithinSameBatchCannotEraseNewerPendingOrSplitRecordedEvidence()
    {
        await using var f = await Fixture.Create();
        await f.History(f.Profile(0, null));
        await f.Send(f.Sample(0), f.Sample(4), f.Sample(2), f.Sample(5));
        f.AssertPossible(Assert.Single(await f.Gaps()), 0, 4);
        Assert.Equal(f.Start.AddSeconds(4), (await f.Db.RuleRuntimeStates.SingleAsync()).PendingRaiseSince);
        Assert.Equal(f.Start.AddSeconds(5), (await f.Db.RuleRuntimeStates.SingleAsync()).LastEvaluatedAt);
    }

    [DatabaseFact]
    public async Task OverflowAtCurrentWatermarkStillResetsPending()
    {
        await using var f = await Fixture.Create();
        await f.History(f.Profile(0, null));
        await f.Send(f.Sample(0));
        Assert.NotNull((await f.Db.RuleRuntimeStates.SingleAsync()).PendingRaiseSince);
        var overflow = f.Sample(0, key: "same-time-overflow") with
        {
            RawStatusCode = 0x480
        };
        await f.Send(overflow);
        Assert.Null((await f.Db.RuleRuntimeStates.SingleAsync()).PendingRaiseSince);
        Assert.Equal("ServerOverflow", Assert.Single(await f.Gaps()).Reason);
    }
}

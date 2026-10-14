using PlantOps.Application.Telemetry;
using PlantOps.Domain.Telemetry;
using Xunit;

namespace PlantOps.Tests;

public sealed class TrendSamplingTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid PointId = Guid.Parse("40000000-0000-0000-0000-000000000009");
    private static readonly Guid OtherPoint = Guid.Parse("40000000-0000-0000-0000-000000000010");

    private static SamplingProfile Profile(double from, double? to = null, double interval = 1000, string mode = "Periodic") => new()
    {
        PointId = PointId,
        EffectiveFrom = Start.AddSeconds(from),
        EffectiveTo = to.HasValue ? Start.AddSeconds(to.Value) : null,
        Mode = mode,
        RevisedSamplingIntervalMs = interval
    };

    private static MeasurementReading Reading(double seconds, string kind = "OpcUa") => new()
    {
        Id = Guid.NewGuid(),
        PointId = PointId,
        EffectiveAt = Start.AddSeconds(seconds),
        ReceivedAt = Start.AddSeconds(seconds),
        Quality = "Good",
        NumericValue = 4,
        SourceKind = kind
    };

    private static MeasurementReading[] Regular() => Enumerable.Range(0, 60).Select(i => Reading(i)).ToArray();

    private static TrendBucket Bucket(IReadOnlyList<MeasurementReading> rows, IReadOnlyList<SamplingProfile> profiles,
        IReadOnlyList<IngestionGap>? gaps = null, double from = 0, double to = 60, int requestedInterval = 1000) =>
        Assert.Single(TrendBuilder.Build(new MeasurementPoint { Id = PointId, ExpectedIntervalMs = requestedInterval }, rows,
            Start.AddSeconds(from), Start.AddSeconds(to), "1m", Start.AddMinutes(2), gaps, profiles).Buckets);

    [Fact]
    public void SufficientCountAtMinuteStartStillHasTimingGap()
    {
        var rows = Enumerable.Range(0, 60).Select(i => Reading(i / 1000.0)).ToArray();
        var bucket = Bucket(rows, [Profile(0)]);
        Assert.Equal(60, bucket.GoodCount);
        Assert.Equal(1.0, bucket.CoverageRatio);
        Assert.True(bucket.HasGap);
    }

    [Fact]
    public void SlowerRevisionDoesNotInheritPriorRawInterval()
    {
        var rows = Enumerable.Range(0, 30).Select(i => Reading(i)).Concat(new[] { Reading(35), Reading(45), Reading(55) }).ToArray();
        var bucket = Bucket(rows, [Profile(0, 30), Profile(30, interval: 10000)], requestedInterval: 100);
        Assert.Equal(33, bucket.GoodCount);
        Assert.Equal(1.0, bucket.CoverageRatio);
        Assert.False(bucket.HasGap);
    }

    [Theory]
    [InlineData(20, 21, true)]
    [InlineData(-10, 0, false)]
    [InlineData(60, 61, false)]
    [InlineData(-1, 1, true)]
    [InlineData(59, 61, true)]
    public void ExplicitLossUsesHalfOpenBucketIntersection(double from, double to, bool expected)
    {
        IngestionGap gap = new()
        {
            PointId = PointId,
            From = Start.AddSeconds(from),
            To = Start.AddSeconds(to)
        };
        var bucket = Bucket(Regular(), [Profile(0)], [gap]);
        Assert.Equal(1.0, bucket.CoverageRatio);
        Assert.Equal(expected, bucket.HasGap);
    }

    [Fact]
    public void ChangesModeStillReportsExplicitLossDuringSilence()
    {
        var rows = new[] { Reading(0), Reading(61) };
        IngestionGap gap = new()
        {
            PointId = PointId,
            From = Start.AddSeconds(20),
            To = Start.AddSeconds(30)
        };
        var buckets = TrendBuilder.Build(new MeasurementPoint { Id = PointId }, rows, Start, Start.AddMinutes(2), "1m",
            Start.AddMinutes(2), [gap], [Profile(0, mode: "Changes", interval: 0)]).Buckets;
        Assert.Null(buckets[0].CoverageRatio);
        Assert.Equal("Changes", buckets[0].SamplingMode);
        Assert.True(buckets[0].HasGap);
        Assert.False(buckets[1].HasGap);
    }

    [Fact]
    public void AnotherPointsProfilesAndLossCannotChangeCoverage()
    {
        var foreign = Profile(0);
        foreign.PointId = OtherPoint;
        IngestionGap gap = new()
        {
            PointId = OtherPoint,
            From = Start,
            To = null
        };
        var bucket = Bucket(Regular(), [Profile(0), foreign], [gap]);
        Assert.Equal("Periodic", bucket.SamplingMode);
        Assert.Equal(1.0, bucket.CoverageRatio);
        Assert.False(bucket.HasGap);
    }

    [Fact]
    public void PartialMinuteIgnoresLossOutsideRequestedWindow()
    {
        IngestionGap gap = new()
        {
            PointId = PointId,
            From = Start.AddSeconds(5),
            To = Start.AddSeconds(10)
        };
        var rows = Enumerable.Range(20, 20).Select(i => Reading(i)).ToArray();
        var bucket = Bucket(rows, [Profile(0)], [gap], from: 20, to: 40);
        Assert.Equal(1.0, bucket.CoverageRatio);
        Assert.False(bucket.HasGap);
    }

    [Fact]
    public void ClosedHistoryWindowRemainsUnknown()
    {
        var bucket = Bucket(Regular(), [Profile(0, 20), Profile(30)]);
        Assert.Equal("Unknown", bucket.SamplingMode);
        Assert.Null(bucket.CoverageRatio);
        Assert.True(bucket.HasGap);
    }

    [Fact]
    public void ChangesSilenceDoesNotInventPeriodicLoss()
    {
        var bucket = Bucket([Reading(10)], [Profile(0, mode: "Changes", interval: 0)]);
        Assert.Equal("Changes", bucket.SamplingMode);
        Assert.Null(bucket.CoverageRatio);
        Assert.False(bucket.HasGap);
    }

    [Fact]
    public void SimulationKeepsItsFixedIntervalCoverage()
    {
        var rows = Enumerable.Range(0, 60).Select(i => Reading(i, "Simulation")).ToArray();
        var bucket = Bucket(rows, [Profile(0, mode: "Changes", interval: 0)]);
        Assert.Equal("Periodic", bucket.SamplingMode);
        Assert.Equal(1.0, bucket.CoverageRatio);
        Assert.False(bucket.HasGap);
    }

    [Fact]
    public void PeriodicToChangesDoesNotExtendPeriodicExpectationIntoSilence()
    {
        var rows = Enumerable.Range(0, 30).Select(i => Reading(i)).Append(Reading(50)).ToArray();
        var bucket = Bucket(rows, [Profile(0, 30), Profile(30, mode: "Changes", interval: 0)]);
        Assert.Equal("Changes", bucket.SamplingMode);
        Assert.Null(bucket.CoverageRatio);
        Assert.False(bucket.HasGap);
    }

    [Fact]
    public void FasterRevisionUsesItsOwnIntervalAndPreservesHistory()
    {
        var rows = Enumerable.Range(0, 15).Select(i => Reading(i * 2))
            .Concat(Enumerable.Range(30, 30).Select(i => Reading(i))).ToArray();
        SamplingProfile[] profiles = [Profile(0, 30, 2000), Profile(30)];
        var bucket = Bucket(rows, profiles, requestedInterval: 10000);
        Assert.Equal(45, bucket.GoodCount);
        Assert.Equal(1.0, bucket.CoverageRatio);
        Assert.False(bucket.HasGap);
        Assert.Equal(2000.0, profiles[0].RevisedSamplingIntervalMs);
        Assert.Equal(Start.AddSeconds(30), profiles[0].EffectiveTo);
        Assert.Null(profiles[1].EffectiveTo);
    }

    [Theory]
    [InlineData(20, true)]
    [InlineData(60, false)]
    public void OpenLossRespectsBucketEnd(double from, bool expected)
    {
        IngestionGap gap = new()
        {
            PointId = PointId,
            From = Start.AddSeconds(from)
        };
        var bucket = Bucket(Regular(), [Profile(0)], [gap]);
        Assert.Equal(expected, bucket.HasGap);
    }

    [Theory]
    [InlineData("Bad", false, null)]
    [InlineData("Uncertain", false, 4.0)]
    [InlineData("Good", true, 4.0)]
    public void UntrustedObservationStillBreaksMinuteContinuity(string quality, bool anomaly, double? value)
    {
        var rows = Regular();
        rows[20].Quality = quality;
        rows[20].TimeAnomaly = anomaly;
        rows[20].NumericValue = value;
        var bucket = Bucket(rows, [Profile(0)]);
        Assert.Equal(59, bucket.GoodCount);
        Assert.True(bucket.HasGap);
    }
}

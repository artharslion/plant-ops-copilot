using PlantOps.Application.Telemetry;
using PlantOps.Domain.Telemetry;
using Xunit;

namespace PlantOps.Tests;

public sealed class RawTrendContinuityTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid PointId = Guid.Parse("50000000-0000-0000-0000-000000000009");

    private static SamplingProfile Profile(double from, double? to, double interval, string mode = "Periodic") => new()
    {
        PointId = PointId,
        EffectiveFrom = Start.AddSeconds(from),
        EffectiveTo = to.HasValue ? Start.AddSeconds(to.Value) : null,
        Mode = mode,
        RevisedSamplingIntervalMs = interval
    };

    private static MeasurementReading Reading(double seconds, string kind = "OpcUa") => new()
    {
        PointId = PointId,
        EffectiveAt = Start.AddSeconds(seconds),
        ReceivedAt = Start.AddSeconds(seconds),
        Quality = "Good",
        NumericValue = 4,
        SourceKind = kind
    };

    private static MeasurementTrend Trend(double[] times, SamplingProfile[] profiles, double from = 0, double to = 60,
        IngestionGap[]? gaps = null, string kind = "OpcUa") => TrendBuilder.Build(
            new MeasurementPoint { Id = PointId, ExpectedIntervalMs = 1000 }, times.Select(x => Reading(x, kind)).ToArray(),
            Start.AddSeconds(from), Start.AddSeconds(to), "raw", Start.AddMinutes(2), gaps, profiles);

    [Theory]
    [InlineData(29, 35, 1000, 10000, false)]
    [InlineData(20, 35, 1000, 10000, true)]
    [InlineData(20, 31, 10000, 1000, false)]
    [InlineData(29, 40, 10000, 1000, true)]
    [InlineData(29, 31.5, 10000, 1000, false)]
    [InlineData(29, 31.501, 10000, 1000, true)]
    public void AdjacentSamplesConsumeEachHistoricalSegment(double prior, double next, double before, double after, bool expected)
    {
        var trend = Trend([prior, next], [Profile(0, 30, before), Profile(30, null, after)], from: prior, to: next + 1);
        Assert.Equal(expected, trend.Samples[1].GapBefore);
    }

    [Fact]
    public void PeriodicExpectationStopsAtChangesBoundary()
    {
        var trend = Trend([29, 55], [Profile(0, 30, 1000), Profile(30, null, 0, "Changes")], from: 29, to: 56);
        Assert.False(trend.Samples[1].GapBefore);
    }

    [Fact]
    public void LeadingWindowConsumesSlowerRevision()
    {
        var trend = Trend([35], [Profile(0, 30, 1000), Profile(30, null, 10000)], from: 29, to: 36);
        Assert.False(trend.LeadingGap);
        Assert.False(trend.Samples[0].GapBefore);
    }

    [Fact]
    public void TrailingWindowRetainsEarlierFastLoss()
    {
        var trend = Trend([20], [Profile(0, 30, 1000), Profile(30, null, 10000)], from: 20, to: 35);
        Assert.True(trend.TrailingGap);
    }

    [Fact]
    public void TrailingWindowDoesNotApplyNewFastIntervalToOldSlowTime()
    {
        var trend = Trend([20], [Profile(0, 30, 10000), Profile(30, null, 1000)], from: 20, to: 31);
        Assert.False(trend.TrailingGap);
    }

    [Fact]
    public void ClosedProfileDoesNotExtendThroughUnknownWindow()
    {
        var trend = Trend([29, 55], [Profile(0, 30, 1000), Profile(50, null, 10000)], from: 29, to: 56);
        Assert.False(trend.Samples[1].GapBefore);
    }

    [Fact]
    public void ForeignPointLossIsIgnoredByAllRawIndicators()
    {
        var gap = new IngestionGap { PointId = Guid.NewGuid(), From = Start, To = null };
        var trend = Trend([0, 1], [Profile(0, null, 1000)], to: 2, gaps: [gap]);
        Assert.False(trend.LeadingGap);
        Assert.False(trend.TrailingGap);
        Assert.All(trend.Samples, x => Assert.False(x.GapBefore));
    }

    [Theory]
    [InlineData(10, 11, true)]
    [InlineData(-1, 0, false)]
    [InlineData(20, 21, false)]
    public void LeadingLossUsesHalfOpenWindow(double from, double to, bool expected)
    {
        var gap = new IngestionGap { PointId = PointId, From = Start.AddSeconds(from), To = Start.AddSeconds(to) };
        var trend = Trend([20], [Profile(0, null, 0, "Changes")], to: 21, gaps: [gap]);
        Assert.Equal(expected, trend.LeadingGap);
    }

    [Theory]
    [InlineData(10, 11, true)]
    [InlineData(20, 21, false)]
    [InlineData(-1, 0, false)]
    public void TrailingLossUsesHalfOpenWindow(double from, double to, bool expected)
    {
        var gap = new IngestionGap { PointId = PointId, From = Start.AddSeconds(from), To = Start.AddSeconds(to) };
        var trend = Trend([0], [Profile(0, null, 0, "Changes")], to: 20, gaps: [gap]);
        Assert.Equal(expected, trend.TrailingGap);
    }

    [Fact]
    public void LossAtCurrentSampleStillMarksGapBefore()
    {
        var gap = new IngestionGap { PointId = PointId, From = Start.AddSeconds(1), To = Start.AddSeconds(1).AddTicks(1) };
        var trend = Trend([0, 1], [Profile(0, null, 0, "Changes")], to: 2, gaps: [gap]);
        Assert.False(trend.Samples[0].GapBefore);
        Assert.True(trend.Samples[1].GapBefore);
    }

    [Fact]
    public void SimulationRetainsFixedInterval()
    {
        var trend = Trend([0, 5], [Profile(0, null, 0, "Changes")], to: 6, kind: "Simulation");
        Assert.True(trend.Samples[1].GapBefore);
        Assert.False(trend.TrailingGap);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidHistoricalIntervalDoesNotFallBackToPointRequest(double interval)
    {
        var trend = Trend([10, 50], [Profile(0, null, interval)], to: 60);
        Assert.False(trend.LeadingGap);
        Assert.False(trend.TrailingGap);
        Assert.All(trend.Samples, x => Assert.False(x.GapBefore));
    }

    [Fact]
    public void MissingHistoryDoesNotFallBackToPointRequest()
    {
        var trend = Trend([10, 50], [], to: 60);
        Assert.False(trend.LeadingGap);
        Assert.False(trend.TrailingGap);
        Assert.All(trend.Samples, x => Assert.False(x.GapBefore));
    }

    [Fact]
    public void OverlappingHistoryIsNotAUniquePeriodicExpectation()
    {
        var trend = Trend([0, 50], [Profile(0, null, 1000), Profile(0, null, 10000)], to: 51);
        Assert.False(trend.Samples[1].GapBefore);
    }

    [Fact]
    public void KnownFastSegmentStillReportsLossBesideOverlap()
    {
        var trend = Trend([0, 50], [Profile(0, null, 1000), Profile(10, null, 10000)], to: 51);
        Assert.True(trend.Samples[1].GapBefore);
    }

    [Fact]
    public void ForeignProfileCannotSuppressKnownExpectation()
    {
        var foreign = Profile(0, null, 0, "Changes");
        foreign.PointId = Guid.NewGuid();
        var trend = Trend([0, 50], [Profile(0, null, 1000), foreign], to: 51);
        Assert.True(trend.Samples[1].GapBefore);
    }

    [Fact]
    public void MultipleRevisionsPreserveInputHistory()
    {
        SamplingProfile[] profiles = [Profile(0, 10, 10000), Profile(10, 20, 0, "Changes"), Profile(20, null, 1000)];
        var trend = Trend([0, 21], profiles, to: 22);
        Assert.False(trend.Samples[1].GapBefore);
        Assert.Equal(Start.AddSeconds(10), profiles[0].EffectiveTo);
        Assert.Equal(10000.0, profiles[0].RevisedSamplingIntervalMs);
        Assert.Null(profiles[2].EffectiveTo);
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(20, false)]
    public void OpenLossUsesTrailingQueryBoundary(double from, bool expected)
    {
        var gap = new IngestionGap { PointId = PointId, From = Start.AddSeconds(from) };
        var trend = Trend([0], [Profile(0, null, 0, "Changes")], to: 20, gaps: [gap]);
        Assert.Equal(expected, trend.TrailingGap);
    }

    [Fact]
    public void LossEndingAtPreviousSampleDoesNotExtendToNextSample()
    {
        var gap = new IngestionGap { PointId = PointId, From = Start.AddSeconds(-1), To = Start };
        var trend = Trend([0, 1], [Profile(0, null, 1000)], to: 2, gaps: [gap]);
        Assert.All(trend.Samples, x => Assert.False(x.GapBefore));
    }

    [Fact]
    public void OverflowStillMarksCurrentObservationInChangesMode()
    {
        var reading = Reading(0);
        reading.RawStatusCode = 0x480;
        var trend = TrendBuilder.Build(new MeasurementPoint { Id = PointId }, [reading], Start, Start.AddSeconds(1),
            "raw", Start.AddMinutes(2), profiles: [Profile(0, null, 0, "Changes")]);
        Assert.True(Assert.Single(trend.Samples).GapBefore);
    }

    [Fact]
    public void NoObservationsRetainExistingEmptyEdgeFlags()
    {
        var trend = Trend([], [Profile(0, null, 0, "Changes")]);
        Assert.Empty(trend.Samples);
        Assert.True(trend.LeadingGap);
        Assert.True(trend.TrailingGap);
    }

    [Fact]
    public void ChangesToPeriodicOnlyChecksThePeriodicPortion()
    {
        var trend = Trend([0, 31], [Profile(0, 30, 0, "Changes"), Profile(30, null, 1000)], to: 32);
        Assert.False(trend.Samples[1].GapBefore);
    }
}

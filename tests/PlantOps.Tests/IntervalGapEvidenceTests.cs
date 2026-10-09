using PlantOps.Application.Telemetry;
using PlantOps.Domain.Telemetry;
using Xunit;

namespace PlantOps.Tests;

public sealed class IntervalGapEvidenceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid PointId = Guid.Parse("50000000-0000-0000-0000-000000000010");

    private static SamplingProfile Profile(double from, double? to, double? interval, string mode = "Periodic") => new()
    {
        PointId = PointId,
        SourceMappingId = Guid.NewGuid(),
        MappingVersion = 3,
        EffectiveFrom = Start.AddSeconds(from),
        EffectiveTo = to.HasValue ? Start.AddSeconds(to.Value) : null,
        Mode = mode,
        RevisedSamplingIntervalMs = interval
    };

    [Fact]
    public void EvidenceReportsWholeClippedSegmentAndHistoricalIdentity()
    {
        var profile = Profile(0, 30, 1000);
        Assert.True(SamplingCoverage.HasIntervalGap(Start.AddSeconds(20), Start.AddSeconds(35), [profile]));
        var evidence = Assert.Single(SamplingCoverage.FindIntervalGapEvidence(Start.AddSeconds(20), Start.AddSeconds(35), [profile]));
        Assert.Equal(Start.AddSeconds(20), evidence.From);
        Assert.Equal(Start.AddSeconds(30), evidence.To);
        Assert.Equal(profile.Id, evidence.SamplingProfileId);
        Assert.Equal(profile.PointId, evidence.PointId);
        Assert.Equal(profile.SourceMappingId, evidence.SourceMappingId);
        Assert.Equal(3, evidence.MappingVersion);
        Assert.Equal(1000, evidence.RevisedSamplingIntervalMs);
    }

    [Theory]
    [InlineData(29, 35, 1000, 10000, -1, -1)]
    [InlineData(20, 35, 1000, 10000, 20, 30)]
    [InlineData(20, 31, 10000, 1000, -1, -1)]
    [InlineData(29, 40, 10000, 1000, 30, 40)]
    [InlineData(29, 31.5, 10000, 1000, -1, -1)]
    [InlineData(29, 31.501, 10000, 1000, 30, 31.501)]
    public void RevisionBoundariesResetTolerance(double from, double to, double before, double after,
        double expectedFrom, double expectedTo)
    {
        var profiles = new[] { Profile(0, 30, before), Profile(30, null, after) };
        var result = SamplingCoverage.FindIntervalGapEvidence(Start.AddSeconds(from), Start.AddSeconds(to), profiles);
        Assert.Equal(expectedFrom >= 0, SamplingCoverage.HasIntervalGap(Start.AddSeconds(from), Start.AddSeconds(to), profiles));
        if (expectedFrom < 0)
        {
            Assert.Empty(result);
        }
        else
        {
            var evidence = Assert.Single(result);
            Assert.Equal(Start.AddSeconds(expectedFrom), evidence.From);
            Assert.Equal(Start.AddSeconds(expectedTo), evidence.To);
        }
    }

    [Fact]
    public void SeparateEvidenceRemainsOrderedWithoutMergingAcrossRevisions()
    {
        var first = Profile(0, 10, 1000);
        var second = Profile(10, 20, 1000);
        var third = Profile(20, null, 1000);
        var result = SamplingCoverage.FindIntervalGapEvidence(Start.AddSeconds(5), Start.AddSeconds(25), [third, first, second]);
        Assert.Equal(new[] { first.Id, second.Id, third.Id }, result.Select(x => x.SamplingProfileId));
        Assert.Equal(new[] { Start.AddSeconds(5), Start.AddSeconds(10), Start.AddSeconds(20) }, result.Select(x => x.From));
        Assert.Equal(new[] { Start.AddSeconds(10), Start.AddSeconds(20), Start.AddSeconds(25) }, result.Select(x => x.To));
    }

    [Theory]
    [InlineData("Changes", 0.0)]
    [InlineData("Unknown", 1000.0)]
    [InlineData("Periodic", 0.0)]
    [InlineData("Periodic", -1.0)]
    [InlineData("Periodic", double.NaN)]
    [InlineData("Periodic", double.PositiveInfinity)]
    [InlineData("Periodic", double.NegativeInfinity)]
    [InlineData("Periodic", null)]
    public void UnsupportedSegmentsDoNotProduceEvidence(string mode, double? interval)
    {
        Assert.Empty(SamplingCoverage.FindIntervalGapEvidence(Start, Start.AddMinutes(1), [Profile(0, null, interval, mode)]));
    }

    [Fact]
    public void OverlapIsExcludedButKnownPartsOnEitherSideRemain()
    {
        var primary = Profile(0, 30, 1000);
        var overlap = Profile(10, 20, 1000);
        var result = SamplingCoverage.FindIntervalGapEvidence(Start, Start.AddSeconds(30), [primary, overlap]);
        Assert.Equal(2, result.Count);
        Assert.Equal(Start.AddSeconds(10), result[0].To);
        Assert.Equal(Start.AddSeconds(20), result[1].From);
        Assert.All(result, x => Assert.Equal(primary.Id, x.SamplingProfileId));
    }

    [Fact]
    public void UnknownAndChangesWindowsAreNotBridged()
    {
        var result = SamplingCoverage.FindIntervalGapEvidence(Start, Start.AddSeconds(50),
            [Profile(0, 10, 1000), Profile(20, 30, 0, "Changes"), Profile(40, null, 1000)]);
        Assert.Equal(2, result.Count);
        Assert.Equal(Start.AddSeconds(10), result[0].To);
        Assert.Equal(Start.AddSeconds(40), result[1].From);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 1.5)]
    public void EmptyReversedAndToleranceWindowsHaveNoEvidence(double from, double to)
    {
        Assert.Empty(SamplingCoverage.FindIntervalGapEvidence(Start.AddSeconds(from), Start.AddSeconds(to), [Profile(0, null, 1000)]));
    }

    [Fact]
    public void ProfilesOutsideHalfOpenWindowDoNotParticipate()
    {
        Assert.Empty(SamplingCoverage.FindIntervalGapEvidence(Start.AddSeconds(10), Start.AddSeconds(20),
            [Profile(0, 10, 1000), Profile(20, null, 1000)]));
        Assert.Empty(SamplingCoverage.FindIntervalGapEvidence(Start, Start.AddSeconds(20), []));
    }

    [Fact]
    public void EvidenceSnapshotsRemainIndependentOfMutableHistory()
    {
        var profile = Profile(0, 10, 1000);
        var originalId = profile.Id;
        var result = SamplingCoverage.FindIntervalGapEvidence(Start, Start.AddSeconds(20), [profile]);
        Assert.Equal(Start.AddSeconds(10), profile.EffectiveTo);
        Assert.Equal(1000, profile.RevisedSamplingIntervalMs);
        profile.Id = Guid.NewGuid();
        profile.RevisedSamplingIntervalMs = 10000;
        profile.EffectiveTo = null;
        var evidence = Assert.Single(result);
        Assert.Equal(originalId, evidence.SamplingProfileId);
        Assert.Equal(1000, evidence.RevisedSamplingIntervalMs);
        Assert.Equal(Start.AddSeconds(10), evidence.To);
    }
}

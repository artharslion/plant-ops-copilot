using PlantOps.Application.Telemetry;
using PlantOps.Domain.Telemetry;
using Xunit;

namespace PlantOps.Tests;

public sealed class SamplingCoverageTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MidMinuteRevisionUsesBothEffectiveIntervals()
    {
        SamplingProfile[] profiles = [
            new() { EffectiveFrom = Start, EffectiveTo = Start.AddSeconds(30), Mode = "Periodic", RevisedSamplingIntervalMs = 2000 },
            new() { EffectiveFrom = Start.AddSeconds(30), EffectiveTo = Start.AddMinutes(1), Mode = "Periodic", RevisedSamplingIntervalMs = 1000 }
        ];
        var result = SamplingCoverage.Calculate(Start, Start.AddMinutes(1), 45, profiles);
        Assert.Equal(45.0, result.ExpectedCount);
        Assert.Equal(1.0, result.CoverageRatio);
        Assert.Equal(1.0, SamplingCoverage.Calculate(Start, Start.AddMinutes(1), 46, profiles).CoverageRatio);
    }

    [Theory]
    [InlineData("Changes")]
    [InlineData("Unknown")]
    public void ChangeNotificationsAndUnknownHistoryDoNotInventCoverage(string mode)
    {
        var result = SamplingCoverage.Calculate(Start, Start.AddMinutes(1), 1,
            [new() { EffectiveFrom = Start, Mode = mode }]);
        Assert.Null(result.CoverageRatio);
        Assert.Null(result.ExpectedCount);
    }

    [Fact]
    public void ClusteredSamplesCannotProveContinuousCoverageEvenIfTheirCountIsSufficient()
    {
        var profile = new SamplingProfile { EffectiveFrom = Start, Mode = "Periodic", RevisedSamplingIntervalMs = 1000 };
        var clustered = Enumerable.Range(0, 60).Select(i => new MeasurementReading
        {
            EffectiveAt = Start.AddMilliseconds(i),
            Quality = "Good",
            NumericValue = 4
        }).ToArray();
        Assert.Equal(1.0, SamplingCoverage.Calculate(Start, Start.AddMinutes(1), 60, [profile]).CoverageRatio);
        Assert.True(SamplingCoverage.HasTimingGap(Start, Start.AddMinutes(1), [profile], clustered));
    }

    [Fact]
    public void MissingAndOverlappingProfilesRemainUnknown()
    {
        var profile = new SamplingProfile { EffectiveFrom = Start.AddSeconds(1), Mode = "Periodic", RevisedSamplingIntervalMs = 1000 };
        Assert.Null(SamplingCoverage.Calculate(Start, Start.AddMinutes(1), 59, [profile]).CoverageRatio);
        profile.EffectiveFrom = Start;
        Assert.Null(SamplingCoverage.Calculate(Start, Start.AddMinutes(1), 60,
            [profile, new() { EffectiveFrom = Start.AddSeconds(30), Mode = "Periodic", RevisedSamplingIntervalMs = 1000 }]).CoverageRatio);
    }
}

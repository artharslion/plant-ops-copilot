using System.Text.Json;
using PlantOps.Application.Telemetry;
using PlantOps.Domain.Telemetry;
using Xunit;

namespace PlantOps.Tests;

public sealed class SamplingProfileTransitionTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Point = Guid.NewGuid();
    private static readonly Guid Mapping = Guid.NewGuid();

    private static SamplingRevision Revision(DateTimeOffset at, long version = 1, string mode = "Periodic") =>
        new(Point, Mapping, version, at, mode, 2000, 2500, 20);

    private static SamplingProfile[] Initial() => SamplingProfileTransitions.Activate([], Revision(Start)).ToArray();

    [Fact]
    public void ExplicitRevisionsCloseOnlyTheOpenIntervalAndPreserveOldMinuteSemantics()
    {
        var history = Initial();
        var before = JsonSerializer.Serialize(history);
        var revised = Revision(Start.AddSeconds(30), 2) with
        {
            RevisedSamplingIntervalMs = 1000,
            RevisedPublishingIntervalMs = 1500,
            RevisedQueueSize = 30
        };
        var next = SamplingProfileTransitions.Activate(history, revised);
        Assert.Equal(before, JsonSerializer.Serialize(history));
        Assert.NotSame(history[0], next[0]);
        Assert.Equal(history[0].Id, next[0].Id);
        Assert.NotEqual(next[0].Id, next[1].Id);
        Assert.Equal(Start.AddSeconds(30), next[0].EffectiveTo);
        Assert.Null(next[1].EffectiveTo);
        Assert.Equal(2000, next[0].RevisedSamplingIntervalMs);
        Assert.Equal(2500, next[0].RevisedPublishingIntervalMs);
        Assert.Equal(20, next[0].RevisedQueueSize);
        Assert.Equal(1000, next[1].RevisedSamplingIntervalMs);
        Assert.Equal(1500, next[1].RevisedPublishingIntervalMs);
        Assert.Equal(30, next[1].RevisedQueueSize);
        Assert.Equal(2, next[1].MappingVersion);
        Assert.Equal(45.0, SamplingCoverage.Calculate(Start, Start.AddMinutes(1), 45, next).ExpectedCount);
        var later = SamplingProfileTransitions.Activate(next, Revision(Start.AddMinutes(1), 3));
        Assert.Equal(45.0, SamplingCoverage.Calculate(Start, Start.AddMinutes(1), 45, later).ExpectedCount);
        Assert.Equal(1.0, SamplingCoverage.Calculate(Start, Start.AddMinutes(1), 45, later).CoverageRatio);
        later[0].RevisedSamplingIntervalMs = 999;
        Assert.Equal(2000, next[0].RevisedSamplingIntervalMs);
    }

    [Fact]
    public void StopAndRestartPreserveUnknownGapRatherThanInventCoverage()
    {
        var history = Initial();
        var closed = SamplingProfileTransitions.Close(history, Point, Start.AddSeconds(20));
        Assert.Null(history[0].EffectiveTo);
        Assert.Equal(Start.AddSeconds(20), Assert.Single(closed).EffectiveTo);
        var restarted = SamplingProfileTransitions.Activate(closed, Revision(Start.AddSeconds(40)));
        Assert.Equal(Start.AddSeconds(20), restarted[0].EffectiveTo);
        Assert.Equal(Start.AddSeconds(40), restarted[1].EffectiveFrom);
        Assert.Null(SamplingCoverage.Calculate(Start, Start.AddMinutes(1), 20, restarted).CoverageRatio);
        Assert.Equal(10.0, SamplingCoverage.Calculate(Start, Start.AddSeconds(20), 10, restarted).ExpectedCount);
        Assert.Null(SamplingCoverage.Calculate(Start.AddSeconds(20), Start.AddSeconds(40), 0, restarted).ExpectedCount);
    }

    [Fact]
    public void ChangesIncludingZeroRevisedSamplingDoNotClaimPeriodicCoverage()
    {
        var next = SamplingProfileTransitions.Activate(Initial(),
            Revision(Start.AddSeconds(30), mode: "Changes") with
            {
                RevisedSamplingIntervalMs = 0
            });
        var result = SamplingCoverage.Calculate(Start, Start.AddMinutes(1), 16, next);
        Assert.Equal("Changes", result.Mode);
        Assert.Null(result.ExpectedCount);
        Assert.Null(result.CoverageRatio);
        Assert.Equal(0, next[1].RevisedSamplingIntervalMs);
    }

    [Fact]
    public void LegacyNullablePublishingAndQueueRemainUnchangedWithoutBackfill()
    {
        var history = Initial();
        history[0].RevisedPublishingIntervalMs = null;
        history[0].RevisedQueueSize = null;
        var next = SamplingProfileTransitions.Activate(history, Revision(Start.AddMinutes(1)));
        Assert.Null(next[0].RevisedPublishingIntervalMs);
        Assert.Null(next[0].RevisedQueueSize);
        Assert.Equal(2500, next[1].RevisedPublishingIntervalMs);
        Assert.Equal(20, next[1].RevisedQueueSize);
    }

    [Fact]
    public void ExplicitRevisedValuesAreNotClampedToRequestedConfigurationLimits()
    {
        var revised = Revision(Start) with
        {
            RevisedSamplingIntervalMs = 500.5,
            RevisedPublishingIntervalMs = 750.25,
            RevisedQueueSize = 10000
        };
        var profile = Assert.Single(SamplingProfileTransitions.Activate([], revised));
        Assert.Equal(500.5, profile.RevisedSamplingIntervalMs);
        Assert.Equal(750.25, profile.RevisedPublishingIntervalMs);
        Assert.Equal(10000, profile.RevisedQueueSize);
    }

    [Fact]
    public void UnknownHistoryAndUnorderedInputArePreservedAndReturnedInTimeOrder()
    {
        var history = SamplingProfileTransitions.Activate(Initial(), Revision(Start.AddSeconds(30))).ToArray();
        history[0].Mode = "Unknown";
        history[0].RevisedSamplingIntervalMs = null;
        Array.Reverse(history);
        var before = JsonSerializer.Serialize(history);
        var next = SamplingProfileTransitions.Activate(history, Revision(Start.AddMinutes(1)));
        Assert.Equal(before, JsonSerializer.Serialize(history));
        Assert.Equal(Start, next[0].EffectiveFrom);
        Assert.Equal("Unknown", next[0].Mode);
        Assert.Null(next[0].RevisedSamplingIntervalMs);
        Assert.Null(SamplingCoverage.Calculate(Start, Start.AddMinutes(1), 30, next).CoverageRatio);
    }

    [Fact]
    public void SourceSwitchAllowsIndependentMappingVersionButRejectsRollbackWhenReturning()
    {
        var first = SamplingProfileTransitions.Activate([], Revision(Start, 5));
        var other = Revision(Start.AddSeconds(20)) with
        {
            SourceMappingId = Guid.NewGuid()
        };
        var second = SamplingProfileTransitions.Activate(first, other);
        RejectUnchanged(second, () => SamplingProfileTransitions.Activate(second, Revision(Start.AddSeconds(40), 4)));
        var returned = SamplingProfileTransitions.Activate(second, Revision(Start.AddSeconds(40), 5));
        Assert.Equal(Mapping, returned[2].SourceMappingId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SwitchAtOrBeforeOpenStartIsRejected(int seconds)
    {
        var history = Initial();
        RejectUnchanged(history, () => SamplingProfileTransitions.Activate(history, Revision(Start.AddSeconds(seconds))));
        RejectUnchanged(history, () => SamplingProfileTransitions.Close(history, Point, Start.AddSeconds(seconds)));
    }

    [Fact]
    public void ClosedHistoryCannotBeReopenedOrOverwrittenButAdjacentStartIsAllowed()
    {
        var closed = SamplingProfileTransitions.Close(Initial(), Point, Start.AddSeconds(30));
        RejectUnchanged(closed, () => SamplingProfileTransitions.Close(closed, Point, Start.AddSeconds(40)));
        RejectUnchanged(closed, () => SamplingProfileTransitions.Activate(closed, Revision(Start.AddSeconds(29))));
        Assert.Equal(Start.AddSeconds(30), SamplingProfileTransitions.Activate(closed, Revision(Start.AddSeconds(30)))[1].EffectiveFrom);
        Assert.Throws<ArgumentException>(() => SamplingProfileTransitions.Close([], Point, Start));
    }

    [Theory]
    [InlineData("sampling-zero")]
    [InlineData("sampling-negative")]
    [InlineData("sampling-nan")]
    [InlineData("sampling-infinity")]
    [InlineData("publishing-zero")]
    [InlineData("publishing-nan")]
    [InlineData("publishing-infinity")]
    [InlineData("queue-zero")]
    [InlineData("queue-large")]
    [InlineData("unknown-mode")]
    [InlineData("empty-point")]
    [InlineData("empty-mapping")]
    [InlineData("zero-version")]
    [InlineData("wrong-point")]
    public void InvalidRevisionIsRejectedWithoutMutatingHistory(string fault)
    {
        var revision = Revision(Start.AddMinutes(1));
        revision = fault switch
        {
            "sampling-zero" => revision with { RevisedSamplingIntervalMs = 0 },
            "sampling-negative" => revision with { Mode = "Changes", RevisedSamplingIntervalMs = -1 },
            "sampling-nan" => revision with { RevisedSamplingIntervalMs = double.NaN },
            "sampling-infinity" => revision with { RevisedSamplingIntervalMs = double.PositiveInfinity },
            "publishing-zero" => revision with { RevisedPublishingIntervalMs = 0 },
            "publishing-nan" => revision with { RevisedPublishingIntervalMs = double.NaN },
            "publishing-infinity" => revision with { RevisedPublishingIntervalMs = double.PositiveInfinity },
            "queue-zero" => revision with { RevisedQueueSize = 0 },
            "queue-large" => revision with { RevisedQueueSize = 10001 },
            "unknown-mode" => revision with { Mode = "Unknown" },
            "empty-point" => revision with { PointId = Guid.Empty },
            "empty-mapping" => revision with { SourceMappingId = Guid.Empty },
            "zero-version" => revision with { MappingVersion = 0 },
            "wrong-point" => revision with { PointId = Guid.NewGuid() },
            _ => throw new InvalidOperationException()
        };
        var history = Initial();
        RejectUnchanged(history, () => SamplingProfileTransitions.Activate(history, revision));
    }

    [Theory]
    [InlineData("overlap")]
    [InlineData("open-middle")]
    [InlineData("duplicate-id")]
    [InlineData("wrong-point")]
    [InlineData("reversed")]
    [InlineData("invalid-mode")]
    [InlineData("empty-id")]
    [InlineData("empty-mapping")]
    [InlineData("zero-version")]
    [InlineData("version-rollback")]
    [InlineData("missing-periodic-interval")]
    [InlineData("invalid-publishing")]
    [InlineData("invalid-queue")]
    public void AmbiguousOrInvalidHistoryIsRejectedWithoutRepair(string fault)
    {
        var history = SamplingProfileTransitions.Activate(Initial(), Revision(Start.AddSeconds(30))).ToArray();
        switch (fault)
        {
            case "overlap":
                history[0].EffectiveTo = Start.AddSeconds(31);
                break;
            case "open-middle":
                history[0].EffectiveTo = null;
                break;
            case "duplicate-id":
                history[1].Id = history[0].Id;
                break;
            case "wrong-point":
                history[0].PointId = Guid.NewGuid();
                break;
            case "reversed":
                history[0].EffectiveTo = Start;
                break;
            case "invalid-mode":
                history[0].Mode = "invalid";
                break;
            case "empty-id":
                history[0].Id = Guid.Empty;
                break;
            case "empty-mapping":
                history[0].SourceMappingId = Guid.Empty;
                break;
            case "zero-version":
                history[0].MappingVersion = 0;
                break;
            case "version-rollback":
                history[0].MappingVersion = 2;
                break;
            case "missing-periodic-interval":
                history[0].RevisedSamplingIntervalMs = null;
                break;
            case "invalid-publishing":
                history[0].RevisedPublishingIntervalMs = 0;
                break;
            case "invalid-queue":
                history[0].RevisedQueueSize = 0;
                break;
        }
        RejectUnchanged(history, () => SamplingProfileTransitions.Activate(history, Revision(Start.AddMinutes(1))));
        RejectUnchanged(history, () => SamplingProfileTransitions.Close(history, Point, Start.AddMinutes(1)));
    }

    private static void RejectUnchanged(IReadOnlyList<SamplingProfile> history, Action action)
    {
        var before = JsonSerializer.Serialize(history);
        Assert.Throws<ArgumentException>(action);
        Assert.Equal(before, JsonSerializer.Serialize(history));
    }
}

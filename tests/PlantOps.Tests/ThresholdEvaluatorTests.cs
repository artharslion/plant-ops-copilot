using PlantOps.Application.Alarms;
using PlantOps.Application.Telemetry;
using PlantOps.Domain.Alarms;
using PlantOps.Domain.Telemetry;
using Xunit;

namespace PlantOps.Tests;

public sealed class ThresholdEvaluatorTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly SourceConfiguration Config = SamplePolicyTests.Configuration;

    private static MeasurementReading Reading(int second, double value = 12.6, string quality = "Good", int delay = 0) =>
        SamplePolicy.ToReading(SamplePolicy.Normalize(Config, value, quality, At.AddSeconds(second), null, At.AddSeconds(second + delay), second.ToString()));

    private static string? Apply(AlarmRule r, RuleRuntimeState s, int second, double value = 12.6, string quality = "Good", bool active = false, int delay = 0) =>
        ThresholdEvaluator.Evaluate(r, s, active, Reading(second, value, quality, delay), Config, At.AddSeconds(second + delay));

    [Fact]
    public void ContinuousRaiseClearAndHysteresisUseStrictThresholds()
    {
        var r = new AlarmRule();
        var s = new RuleRuntimeState();
        Assert.Null(Apply(r, s, 0, 10));
        Assert.Null(s.PendingRaiseSince);
        foreach (var t in new[] { 1, 2, 3 })
        {
            Assert.Null(Apply(r, s, t));
        }

        Assert.Equal("Raised", Apply(r, s, 4));
        Assert.Null(Apply(r, s, 5, 8, active: true));
        Assert.Null(s.PendingClearSince);
        for (var t = 6; t < 11; t++)
        {
            Assert.Null(Apply(r, s, t, 6, active: true));
        }

        Assert.Equal("Recovered", Apply(r, s, 11, 6, active: true));
    }

    [Theory]
    [InlineData("Bad", 0)]
    [InlineData("Uncertain", 0)]
    [InlineData("Good", 6)]
    public void InvalidInputInterruptsBothTimersAndNeverRecovers(string quality, int delay)
    {
        var r = new AlarmRule();
        var s = new RuleRuntimeState();
        Apply(r, s, 0);
        Apply(r, s, 1);
        Assert.Null(Apply(r, s, 2, quality: quality, delay: delay));
        Assert.Null(s.PendingRaiseSince);
        Assert.False(s.LastInputTrusted);
        Apply(r, s, 3, 6, active: true);
        Assert.Null(Apply(r, s, 4, 6, quality, active: true, delay: delay));
        Assert.Null(s.PendingClearSince);
    }

    [Fact]
    public void LateSamplesAreHistoryOnlyAndGapsRestartPendingDuration()
    {
        var r = new AlarmRule();
        var s = new RuleRuntimeState();
        Apply(r, s, 10);
        Apply(r, s, 11);
        Assert.Null(Apply(r, s, 9, 4));
        Assert.Equal(At.AddSeconds(10), s.PendingRaiseSince);
        Assert.Equal(At.AddSeconds(11), s.LastEvaluatedAt);
        Assert.Null(Apply(r, s, 11, 4));
        Assert.Equal(At.AddSeconds(10), s.PendingRaiseSince);
        Assert.Null(Apply(r, s, 20));
        Assert.Equal(At.AddSeconds(20), s.PendingRaiseSince);
        Assert.Null(Apply(r, s, 21));
        Assert.Null(Apply(r, s, 22));
        Assert.Equal("Raised", Apply(r, s, 23));
    }

    [Fact]
    public void RestartAndMappingVersionChangesCannotContinueOldPendingDuration()
    {
        var r = new AlarmRule();
        var s = new RuleRuntimeState();
        Apply(r, s, 0);
        Apply(r, s, 1);
        ThresholdEvaluator.ResetPending(s);
        Assert.Null(Apply(r, s, 2));
        Assert.Equal(At.AddSeconds(2), s.PendingRaiseSince);
        var changed = Config with
        {
            MappingVersion = Config.MappingVersion + 1
        };
        Assert.Null(ThresholdEvaluator.Evaluate(r, s, false, Reading(3), changed, At.AddSeconds(3)));
        Assert.Equal(At.AddSeconds(3), s.PendingRaiseSince);

    }

    [Fact]
    public void FutureTimeAndNonfiniteInputDoNotTransitionActiveAlarm()
    {
        var r = new AlarmRule();
        var s = new RuleRuntimeState();
        Apply(r, s, 0, 6, active: true);
        var future = Reading(60);
        future.ReceivedAt = At.AddSeconds(1);
        future.TimeAnomaly = true;
        Assert.Null(ThresholdEvaluator.Evaluate(r, s, true, future, Config, At.AddSeconds(1)));
        Assert.Null(s.PendingClearSince);
        Assert.Equal(At, s.LastEvaluatedAt);
        var invalid = Reading(2, double.NaN);
        Assert.Equal("Bad", invalid.Quality);
        Assert.Null(ThresholdEvaluator.Evaluate(r, s, true, invalid, Config, At.AddSeconds(2)));
        Assert.False(s.LastInputTrusted);
    }

    [Fact]
    public void ZeroDurationsTransitionOnFirstTrustedSample()
    {
        var r = new AlarmRule { RaiseForMs = 0, ClearForMs = 0 };
        var s = new RuleRuntimeState();
        Assert.Equal("Raised", Apply(r, s, 0));
        Assert.Equal("Recovered", Apply(r, s, 1, 6, active: true));
    }

    [Fact]
    public void QueuedSampleThatBecameStaleBeforeEvaluationCannotRaise()
    {
        var r = new AlarmRule { RaiseForMs = 0 };
        var s = new RuleRuntimeState();
        Assert.Null(ThresholdEvaluator.Evaluate(r, s, false, Reading(0), Config, At.AddSeconds(6)));
        Assert.False(s.LastInputTrusted);
        Assert.Null(s.PendingRaiseSince);
    }

    [Fact]
    public void StaleSilenceResetsTimerEvenWhenRuleAllowsLongerSampleGap()
    {
        var rule = new AlarmRule { MaxSampleGapMs = 30000 };
        var state = new RuleRuntimeState();
        Assert.Null(Apply(rule, state, 0));
        Assert.Null(Apply(rule, state, 1));
        Assert.Null(Apply(rule, state, 10));
        Assert.Equal(At.AddSeconds(10), state.PendingRaiseSince);
    }
}



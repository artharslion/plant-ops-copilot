using PlantOps.Application.Telemetry;
using PlantOps.Domain.Telemetry;
using Xunit;

namespace PlantOps.Tests;

public sealed class SamplePolicyTests
{
    internal static readonly SourceConfiguration Configuration = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
        "mm/s", 1000, 5000, Guid.NewGuid(), 1, "Simulation", Guid.NewGuid(), 1, "P101.VibrationRms", "mm/s", 1, 0,
        "{\"schemaVersion\":1,\"scenario\":\"GoldenPath\"}");

    private static readonly DateTimeOffset At = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidNumbersBecomeBadAndCannotBecomeTrusted(double value)
    {
        var reading = SamplePolicy.ToReading(SamplePolicy.Normalize(Configuration, value, "Good", At, null, At, "invalid"));
        Assert.Equal("Bad", reading.Quality);
        Assert.Null(reading.NumericValue);
        Assert.False(SamplePolicy.IsGood(reading));
    }

    [Fact]
    public void MinimumProtocolTimesAreMissingRatherThanAncientSamples()
    {
        var sample = SamplePolicy.Normalize(Configuration, 4, "Good", DateTimeOffset.MinValue, At, At.AddSeconds(1), "missing-source");
        Assert.Null(sample.SourceTimestamp);
        Assert.Equal("Server", SamplePolicy.ToReading(sample).TimestampOrigin);
        sample = SamplePolicy.Normalize(Configuration, 4, "Good", DateTimeOffset.MinValue, DateTimeOffset.MinValue, At, "missing-both");
        Assert.Null(sample.SourceTimestamp);
        Assert.Null(sample.ServerTimestamp);
        Assert.Equal("Receive", SamplePolicy.ToReading(sample).TimestampOrigin);
    }

    [Fact]
    public void ScaleOffsetAndTimestampFallbackAreExplicit()
    {
        var sample = SamplePolicy.Normalize(Configuration with
        {
            Scale = 2,
            Offset = 1
        }, 4, "Good", null, At, At.AddSeconds(1), "server");
        var reading = SamplePolicy.ToReading(sample);
        Assert.Equal(9, reading.NumericValue);
        Assert.Equal("Server", reading.TimestampOrigin);
        Assert.Equal(At, reading.EffectiveAt);
        Assert.Equal("Receive", SamplePolicy.ToReading(sample with
        {
            ServerTimestamp = null
        }).TimestampOrigin);
    }

    [Fact]
    public void BadUncertainFutureAndOutOfOrderNeverReplaceLastGood()
    {
        var state = new PointLatestState();
        var good = SamplePolicy.ToReading(SamplePolicy.Normalize(Configuration, 4, "Good", At, null, At, "good"));
        SamplePolicy.Apply(state, good, 5000);
        foreach (var quality in new[] { "Bad", "Uncertain" })
        {
            SamplePolicy.Apply(state, SamplePolicy.ToReading(SamplePolicy.Normalize(Configuration, 99, quality, At.AddSeconds(1), null, At.AddSeconds(1), quality)), 5000);
        }

        Assert.Equal(good.Id, state.LastGoodReadingId);
        Assert.Equal(4, state.LastGoodValue);
        Assert.Equal("Bad", state.LatestObservedQuality);
        var future = SamplePolicy.ToReading(SamplePolicy.Normalize(Configuration, 100, "Good", At.AddHours(1), null, At.AddSeconds(2), "future"));
        SamplePolicy.Apply(state, future, 5000);
        Assert.True(future.TimeAnomaly);
        Assert.Equal(At.AddSeconds(1), state.LatestObservedEffectiveAt);
        SamplePolicy.Apply(state, withReading(At.AddSeconds(-10)), 5000);
        Assert.Equal(At.AddSeconds(1), state.LatestObservedEffectiveAt);
        Assert.Equal(At, state.LastTrustedEffectiveAt);
        Assert.False(SamplePolicy.IsFresh(At, At.AddSeconds(6), 5000));
    }

    private static MeasurementReading withReading(DateTimeOffset at) =>
        SamplePolicy.ToReading(SamplePolicy.Normalize(Configuration, 1, "Good", at, null, At.AddSeconds(3), "old"));
}

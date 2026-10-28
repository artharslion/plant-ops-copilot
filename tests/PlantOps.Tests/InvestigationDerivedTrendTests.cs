using PlantOps.Application.AI;
using PlantOps.Domain.Telemetry;
using Xunit;

namespace PlantOps.Tests;

public sealed class InvestigationDerivedTrendTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);
    private static readonly InvestigationPointSnapshot Point = new()
    {
        PointId = Guid.NewGuid(),
        Code = "TEMP",
        Unit = "degC",
        PointVersion = 2,
        SourceId = Guid.NewGuid(),
        SourceVersion = 3,
        MappingId = Guid.NewGuid(),
        MappingVersion = 4,
        SourceKind = "Simulation",
        AsOf = Start.AddSeconds(10),
        StaleAfterMs = 5000
    };

    private static MeasurementReading Row(int second, double value) => new()
    {
        PointId = Point.PointId,
        CanonicalUnit = Point.Unit,
        PointVersion = Point.PointVersion,
        DataSourceId = Point.SourceId!.Value,
        SourceVersion = Point.SourceVersion,
        SourceMappingId = Point.MappingId!.Value,
        MappingVersion = Point.MappingVersion!.Value,
        SourceKind = Point.SourceKind!,
        Quality = "Good",
        TimestampOrigin = "Source",
        EffectiveAt = Start.AddSeconds(second),
        ReceivedAt = Start.AddSeconds(second),
        SourceTimestamp = Start.AddSeconds(second),
        NumericValue = value
    };

    private static InvestigationDerivedTrend Calculate(MeasurementReading[] rows, IngestionGap[]? gaps = null,
        bool truncated = false, InvestigationPointSnapshot? point = null, SamplingProfile[]? profiles = null) =>
        InvestigationTrendPolicy.Calculate(point ?? Point, Guid.NewGuid(), "event", Start, Start.AddSeconds(10),
            Start.AddSeconds(11), rows, gaps ?? [], profiles ?? [], 1000, truncated);

    [Fact]
    public void OrderedHeatingIncludesPlateausExactChangeRateAndProvenance()
    {
        var rows = new[] { Row(0, 60), Row(1, 60), Row(2, 75), Row(3, 75), Row(4, 90) };
        var result = Calculate(rows.Reverse().ToArray());
        Assert.Equal(30, result.Change!.Change);
        Assert.Equal(450, result.Change.RatePerMinute);
        var rise = Assert.Single(result.RiseSegments);
        Assert.Equal(2, rise.IncreaseCount);
        Assert.Equal(5, rise.SampleCount);
        Assert.Equal(rows[0].Id, rise.FirstReadingId);
        Assert.Equal(rows[^1].Id, rise.LastReadingId);
        Assert.Equal(rows.Select(x => x.Id), result.Samples.Select(x => x.Id));
        Assert.Equal(InvestigationTrendPolicy.Version, result.AlgorithmVersion);
        Assert.Empty(result.Reasons);
    }

    [Theory]
    [InlineData("Bad")]
    [InlineData("Uncertain")]
    [InlineData("UnknownSourceVersion")]
    [InlineData("UnknownPointVersion")]
    [InlineData("ChangedMapping")]
    [InlineData("ChangedSourceVersion")]
    [InlineData("ChangedPointVersion")]
    [InlineData("ReceiveTime")]
    [InlineData("TimeAnomaly")]
    [InlineData("Overflow")]
    [InlineData("Gap")]
    [InlineData("IntervalGap")]
    [InlineData("DuplicateTime")]
    [InlineData("Truncated")]
    [InlineData("ArrivedAfterAsOf")]
    [InlineData("NonFinite")]
    public void UnverifiableMiddleSampleCannotSupportAnEndToEndRise(string scenario)
    {
        var rows = new[] { Row(0, 60), Row(1, 75), Row(2, 90) };
        IngestionGap[] gaps = [];
        switch (scenario)
        {
            case "Bad":
                rows[1].Quality = "Bad";
                break;
            case "Uncertain":
                rows[1].Quality = "Uncertain";
                break;
            case "UnknownSourceVersion":
                rows[1].SourceVersion = null;
                break;
            case "UnknownPointVersion":
                rows[1].PointVersion = null;
                break;
            case "ChangedMapping":
                rows[1].MappingVersion++;
                break;
            case "ChangedSourceVersion":
                rows[1].SourceVersion++;
                break;
            case "ChangedPointVersion":
                rows[1].PointVersion++;
                break;
            case "ReceiveTime":
                rows[1].TimestampOrigin = "Receive";
                break;
            case "TimeAnomaly":
                rows[1].TimeAnomaly = true;
                break;
            case "Overflow":
                rows[1].RawStatusCode = 0x480;
                break;
            case "Gap":
                gaps = [new() { PointId = Point.PointId, From = Start.AddMilliseconds(500), To = Start.AddSeconds(1) }];
                break;
            case "IntervalGap":
                rows[2].EffectiveAt = Start.AddSeconds(4);
                rows[2].ReceivedAt = rows[2].EffectiveAt;
                break;
            case "DuplicateTime":
                rows[1].EffectiveAt = rows[0].EffectiveAt;
                break;
            case "ArrivedAfterAsOf":
                rows[1].ReceivedAt = Point.AsOf.AddSeconds(1);
                break;
            case "NonFinite":
                rows[1].NumericValue = double.PositiveInfinity;
                break;
        }
        var result = Calculate(rows, gaps, scenario == "Truncated");
        Assert.Null(result.Change);
        Assert.Empty(result.RiseSegments);
        Assert.NotEmpty(result.Reasons);
    }

    [Fact]
    public void BadPointOrEarlierBadValueDoesNotEraseALaterVerifiableSegment()
    {
        var rows = new[] { Row(0, 100), Row(1, 60), Row(2, 75), Row(3, 90) };
        rows[0].Quality = "Bad";
        var result = Calculate(rows);
        Assert.Null(result.Change);
        var rise = Assert.Single(result.RiseSegments);
        Assert.Equal(rows[1].Id, rise.FirstReadingId);
        Assert.Equal(30, rise.Change);
    }

    [Theory]
    [InlineData(60, 60, 60)]
    [InlineData(90, 75, 60)]
    [InlineData(60, 90, 75)]
    public void FlatFallingOrSingleExcursionDoesNotBecomeSustainedRise(double first, double middle, double last)
    {
        var result = Calculate([Row(0, first), Row(1, middle), Row(2, last)]);
        Assert.NotNull(result.Change);
        Assert.Empty(result.RiseSegments);
    }

    [Theory]
    [InlineData("Periodic", false, true)]
    [InlineData("Changes", false, false)]
    [InlineData("Unknown", false, false)]
    [InlineData("Periodic", true, false)]
    public void ProtocolContinuityRequiresOneVerifiedPeriodicProfile(string mode, bool transition, bool expected)
    {
        var profile = new SamplingProfile
        {
            PointId = Point.PointId,
            SourceMappingId = Point.MappingId!.Value,
            MappingVersion = Point.MappingVersion!.Value,
            EffectiveFrom = Start,
            Mode = mode,
            RevisedSamplingIntervalMs = 1000,
            EffectiveTo = transition ? Start.AddSeconds(1) : null
        };
        var rows = new[] { Row(0, 60), Row(1, 75), Row(2, 90) };
        foreach (var row in rows)
        {
            row.SourceKind = "OpcUa";
        }
        var result = Calculate(rows, point: Point with
        {
            SourceKind = "OpcUa"
        }, profiles: [profile]);
        Assert.Equal(expected, result.Change != null);
        Assert.Equal(expected, result.RiseSegments.Length > 0);
    }
}

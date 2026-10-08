using System.Text.Json;
using Opc.Ua;
using PlantOps.Application.Telemetry;
using PlantOps.Integrations.OpcUa;
using Xunit;

namespace PlantOps.Tests;

public sealed class OpcUaNormalizationTests
{
    private static readonly DateTimeOffset Received = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);

    private static readonly SourceConfiguration Configuration = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
        "mm/s", 1000, 5000, Guid.NewGuid(), 1, "OpcUa", Guid.NewGuid(), 1, "node", "mm/s", 1, 0, "{}");

    private static NormalizedSample Normalize(object? value, uint status = 0, DateTime? source = null,
        DateTime? server = null, Guid? envelope = null) => OpcUaNormalizer.Normalize(Configuration,
        new DataValue(new Variant(value), status, source ?? Received.UtcDateTime, server ?? Received.UtcDateTime), Received, envelope ?? Guid.NewGuid());

    [Fact]
    public void NullBadValuePreservesProtocolSymbolAndKeepsNumericRejectionSeparate()
    {
        var bad = Normalize(null, StatusCodes.BadSensorFailure);
        // SDK symbolic lookup is initialized by Core during other tests. Both SDK
        // renderings denote the same protocol status; numeric rejection never does.
        Assert.Contains(bad.StatusSymbol, new[] { "BadSensorFailure", "808C0000" });
        Assert.Equal(StatusCodes.BadSensorFailure, bad.RawStatusCode);
        Assert.Null(bad.NumericValue);
        using var raw = JsonDocument.Parse(bad.RawValueJson);
        Assert.Equal("Null", raw.RootElement.GetProperty("type").GetString());
        Assert.Equal("UnsupportedOrInvalidNumericValue", raw.RootElement.GetProperty("normalizationError").GetString());
        var invalidGood = Normalize("unsupported");
        Assert.Contains(invalidGood.StatusSymbol, new[] { "Good", "00000000" });
        Assert.Equal("Bad", invalidGood.Quality);
    }

    [Fact]
    public void NumericBoundariesPreserveOriginalTypeAndRejectLossOfIntegerPrecision()
    {
        foreach (var value in new object[]
        {
            byte.MaxValue, sbyte.MinValue, short.MinValue, ushort.MaxValue,
            int.MinValue, uint.MaxValue, 9007199254740992L, 9007199254740992UL, 1.5f, 1.5d
        })
        {
            var sample = Normalize(value);
            Assert.Equal("Good", sample.Quality);
            Assert.NotNull(sample.NumericValue);
            Assert.Contains(value.GetType().Name, sample.RawValueJson);
        }
        foreach (var value in new object[] { 9007199254740993L, ulong.MaxValue, true, "12.6", new[] { 1.0 }, double.NaN, double.PositiveInfinity })
        {
            var sample = Normalize(value);
            Assert.Equal("Bad", sample.Quality);
            Assert.Null(sample.NumericValue);
            using var raw = JsonDocument.Parse(sample.RawValueJson);
            Assert.Equal(value.GetType().Name, raw.RootElement.GetProperty("type").GetString());
        }
        Assert.Contains(ulong.MaxValue.ToString(), Normalize(ulong.MaxValue).RawValueJson);
    }

    [Fact]
    public void RawStatusAndInformationBitsAreIndependentFromSeverity()
    {
        var overflow = Normalize(12.6, 0x480);
        Assert.Equal("Good", overflow.Quality);
        Assert.Equal(0x480u, overflow.RawStatusCode);
        Assert.False(SamplePolicy.IsGood(SamplePolicy.ToReading(overflow)));
        Assert.Equal("Uncertain", Normalize(12.6, StatusCodes.UncertainSensorNotAccurate).Quality);
        Assert.Equal("Bad", Normalize(12.6, StatusCodes.BadSensorFailure).Quality);
        Assert.Null(Normalize(12.6, StatusCodes.BadSensorFailure).NumericValue);
        Assert.False(SamplePolicy.HasOverflow(0x80000080));
    }

    [Fact]
    public void SourceTimeDeduplicationIgnoresChangingServerTimeButIncludesValueTypeStatusAndMappingVersion()
    {
        var first = Normalize(4.0);
        Assert.Equal(first.SourceEventKey, Normalize(4.0, server: Received.AddSeconds(1).UtcDateTime).SourceEventKey);
        Assert.NotEqual(first.SourceEventKey, Normalize(4.0, source: Received.AddSeconds(1).UtcDateTime).SourceEventKey);
        Assert.NotEqual(first.SourceEventKey, Normalize(4.0f).SourceEventKey);
        Assert.NotEqual(first.SourceEventKey, Normalize(4.0, 0x480).SourceEventKey);
        var versionChanged = OpcUaNormalizer.Normalize(Configuration with
        {
            MappingVersion = 2
        },
            new DataValue(new Variant(4.0), 0, Received.UtcDateTime, Received.UtcDateTime), Received, Guid.NewGuid());
        Assert.NotEqual(first.SourceEventKey, versionChanged.SourceEventKey);
    }

    [Fact]
    public void MissingTimesUseStableEnvelopeAndFutureTimeCannotUpdateLastGood()
    {
        var envelope = Guid.NewGuid();
        var missing = Normalize(4.0, source: DateTime.MinValue, server: DateTime.MinValue, envelope: envelope);
        Assert.Null(missing.SourceTimestamp);
        Assert.Null(missing.ServerTimestamp);
        Assert.Equal("Receive", SamplePolicy.ToReading(missing).TimestampOrigin);
        Assert.Equal(missing.SourceEventKey, Normalize(4.0, source: DateTime.MinValue, server: DateTime.MinValue, envelope: envelope).SourceEventKey);
        Assert.NotEqual(missing.SourceEventKey, Normalize(4.0, source: DateTime.MinValue, server: DateTime.MinValue).SourceEventKey);
        var future = SamplePolicy.ToReading(Normalize(12.6, source: Received.AddSeconds(6).UtcDateTime));
        Assert.True(future.TimeAnomaly);
        Assert.False(SamplePolicy.IsGood(future));
    }
}

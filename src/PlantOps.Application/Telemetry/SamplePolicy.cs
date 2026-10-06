using System.Globalization;
using System.Text.Json;
using PlantOps.Domain.Telemetry;

namespace PlantOps.Application.Telemetry;

public static class SamplePolicy
{
    public static NormalizedSample Normalize(SourceConfiguration config, double? raw, string quality,
        DateTimeOffset? sourceAt, DateTimeOffset? serverAt, DateTimeOffset receivedAt, string eventKey,
        uint? status = null, string? symbol = null)
    {
        var value = raw * config.Scale + config.Offset;
        if (quality is not ("Good" or "Uncertain" or "Bad") || !value.HasValue || !double.IsFinite(value.Value))
        { quality = "Bad"; value = null; symbol ??= "InvalidValue"; }
        if (quality == "Bad") value = null;
        return new(config, value, quality,
            JsonSerializer.Serialize(new { value = raw?.ToString("R", CultureInfo.InvariantCulture) }), status, symbol,
            ValidTimestamp(sourceAt), ValidTimestamp(serverAt), receivedAt.ToUniversalTime(), eventKey);
    }
    public static DateTimeOffset? ValidTimestamp(DateTimeOffset? at) =>
        at.HasValue && at.Value > DateTimeOffset.MinValue ? at.Value.ToUniversalTime() : null;
    public static MeasurementReading ToReading(NormalizedSample sample)
    {
        var c = sample.Configuration;
        var effective = sample.SourceTimestamp ?? sample.ServerTimestamp ?? sample.ReceivedAt;
        return new MeasurementReading {
            PlantId = c.PlantId, AssetId = c.AssetId, PointId = c.PointId, NumericValue = sample.NumericValue,
            CanonicalUnit = c.CanonicalUnit, RawValueJson = sample.RawValueJson, RawUnit = c.RawUnit,
            SourceKind = c.SourceKind, DataSourceId = c.DataSourceId, SourceMappingId = c.MappingId, MappingVersion = c.MappingVersion,
            RawStatusCode = sample.RawStatusCode, StatusSymbol = sample.StatusSymbol, Quality = sample.Quality,
            SourceTimestamp = sample.SourceTimestamp, ServerTimestamp = sample.ServerTimestamp, ReceivedAt = sample.ReceivedAt,
            EffectiveAt = effective, EffectiveDate = DateOnly.FromDateTime(effective.UtcDateTime), TimestampOrigin = sample.SourceTimestamp.HasValue ? "Source" : sample.ServerTimestamp.HasValue ? "Server" : "Receive",
            SourceEventKey = sample.SourceEventKey, TimeAnomaly = effective > sample.ReceivedAt.AddSeconds(5)
        };
    }
    public static bool IsFresh(DateTimeOffset at, DateTimeOffset now, int staleMs) => at <= now.AddSeconds(5) && now - at <= TimeSpan.FromMilliseconds(staleMs);
    public static bool IsGood(MeasurementReading reading) => reading.Quality == "Good" && reading.NumericValue.HasValue && double.IsFinite(reading.NumericValue.Value) && !reading.TimeAnomaly;
    public static void Apply(PointLatestState state, MeasurementReading reading, int staleMs)
    {
        if (reading.ReceivedAt > state.LastReceivedAt) state.LastReceivedAt = reading.ReceivedAt;
        if (reading.TimeAnomaly) return;
        if (!state.LatestObservedEffectiveAt.HasValue || reading.EffectiveAt > state.LatestObservedEffectiveAt
            || reading.EffectiveAt == state.LatestObservedEffectiveAt && reading.ReceivedAt > state.LatestObservedReceivedAt)
        {
            state.LatestObservedReadingId = reading.Id; state.LatestObservedValue = reading.NumericValue;
            state.LatestObservedQuality = reading.Quality; state.LatestObservedEffectiveAt = reading.EffectiveAt;
            state.LatestObservedReceivedAt = reading.ReceivedAt;
        }
        if (IsGood(reading) && IsFresh(reading.EffectiveAt, reading.ReceivedAt, staleMs)
            && (!state.LastTrustedEffectiveAt.HasValue || reading.EffectiveAt > state.LastTrustedEffectiveAt
                || reading.EffectiveAt == state.LastTrustedEffectiveAt && reading.ReceivedAt > state.LastGoodReceivedAt))
        {
            state.LastGoodReadingId = reading.Id; state.LastGoodValue = reading.NumericValue;
            state.LastTrustedEffectiveAt = reading.EffectiveAt; state.LastGoodReceivedAt = reading.ReceivedAt;
        }
    }
}

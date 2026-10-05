using PlantOps.Domain.Telemetry;

namespace PlantOps.Application.Telemetry;

public static class TrendBuilder
{
    public static MeasurementTrend Build(MeasurementPoint point, IReadOnlyList<MeasurementReading> readings,
        DateTimeOffset from, DateTimeOffset to, string bucket, DateTimeOffset checkedAt)
    {
        var ordered = readings.OrderBy(x => x.EffectiveAt).ThenBy(x => x.ReceivedAt).ThenBy(x => x.Id).ToList();
        var tolerance = TimeSpan.FromMilliseconds(point.ExpectedIntervalMs * 1.5);
        var samples = new List<TrendSample>();
        DateTimeOffset prior = from;
        foreach (var r in ordered)
        {
            samples.Add(new(r.Id, r.EffectiveAt, r.ReceivedAt, r.NumericValue, r.Quality, r.TimeAnomaly,
                r.EffectiveAt - prior > tolerance, r.DataSourceId, r.SourceMappingId, r.MappingVersion, r.SourceKind,
                r.TimestampOrigin, r.SourceTimestamp, r.ServerTimestamp, r.RawStatusCode, r.StatusSymbol));
            prior = r.EffectiveAt;
        }
        var leading = ordered.Count == 0 || ordered[0].EffectiveAt - from > tolerance;
        var trailing = ordered.Count == 0 || to - ordered[^1].EffectiveAt > tolerance;
        var buckets = new List<TrendBucket>();
        if (bucket == "1m")
        {
            var groups = ordered.GroupBy(r => FloorMinute(r.EffectiveAt)).ToDictionary(g => g.Key, g => g.ToList());
            for (var start = FloorMinute(from); start < to; start = start.AddMinutes(1))
            {
                var rows = groups.GetValueOrDefault(start) ?? [];
                var values = rows.Where(SamplePolicy.IsGood).Select(x => x.NumericValue!.Value).ToArray();
                var coveredFrom = start < from ? from : start;
                var coveredTo = start.AddMinutes(1) > to ? to : start.AddMinutes(1);
                var expected = Math.Max(1, Math.Ceiling((coveredTo - coveredFrom).TotalMilliseconds / point.ExpectedIntervalMs));
                var hasGap = rows.Count < expected || rows.Any(x => x.Quality != "Good" || x.TimeAnomaly || x.NumericValue == null)
                    || samples.Any(x => x.EffectiveAt >= start && x.EffectiveAt < start.AddMinutes(1) && x.GapBefore);
                buckets.Add(new(start, values.Length == 0 ? null : values.Min(), values.Length == 0 ? null : values.Max(),
                    values.Length == 0 ? null : values.Average(), values.Length, rows.Count(x => x.Quality == "Uncertain"),
                    rows.Count(x => x.Quality == "Bad"), rows.Count(x => x.TimeAnomaly), hasGap, Math.Min(1, values.Length / expected)));
            }
        }
        return new(point.Id, point.CanonicalUnit, from, to, bucket, checkedAt,
            bucket == "raw" ? samples : [], buckets, leading, trailing);
    }
    private static DateTimeOffset FloorMinute(DateTimeOffset at) => new(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0, TimeSpan.Zero);
}

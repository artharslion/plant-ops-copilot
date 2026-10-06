using PlantOps.Domain.Telemetry;

namespace PlantOps.Application.Telemetry;

public static class TrendBuilder
{
    public static MeasurementTrend Build(MeasurementPoint point, IReadOnlyList<MeasurementReading> readings,
        DateTimeOffset from, DateTimeOffset to, string bucket, DateTimeOffset checkedAt, IReadOnlyList<IngestionGap>? gaps = null,
        IReadOnlyList<SamplingProfile>? profiles = null)
    {
        var ordered = readings.OrderBy(x => x.EffectiveAt).ThenBy(x => x.ReceivedAt).ThenBy(x => x.Id).ToList();
        var protocol = ordered.Any(x => x.SourceKind == "OpcUa");
        double? IntervalAt(DateTimeOffset at)
        {
            if (!protocol)
            {
                return point.ExpectedIntervalMs;
            }
            var matching = profiles?.Where(x => x.PointId == point.Id && x.EffectiveFrom <= at
                && (x.EffectiveTo == null || x.EffectiveTo > at)).ToArray() ?? [];
            return matching.Length == 1 && matching[0].Mode == "Periodic" ? matching[0].RevisedSamplingIntervalMs : null;
        }
        var samples = new List<TrendSample>();
        DateTimeOffset prior = from;
        foreach (var r in ordered)
        {
            var interval = IntervalAt(prior);
            samples.Add(new(r.Id, r.EffectiveAt, r.ReceivedAt, r.NumericValue, r.Quality, r.TimeAnomaly,
                SamplePolicy.HasOverflow(r.RawStatusCode) || interval.HasValue && r.EffectiveAt - prior > TimeSpan.FromMilliseconds(interval.Value * 1.5)
                    || (gaps?.Any(g => g.From <= r.EffectiveAt && (g.To == null || g.To > prior)) ?? false), r.DataSourceId, r.SourceMappingId, r.MappingVersion, r.SourceKind,
                r.TimestampOrigin, r.SourceTimestamp, r.ServerTimestamp, r.RawStatusCode, r.StatusSymbol));
            prior = r.EffectiveAt;
        }
        var leadingInterval = IntervalAt(from);
        var trailingInterval = IntervalAt(to.AddTicks(-1));
        var leading = ordered.Count == 0 || leadingInterval.HasValue && ordered[0].EffectiveAt - from > TimeSpan.FromMilliseconds(leadingInterval.Value * 1.5)
            || (gaps?.Any(g => g.From <= from && (g.To == null || g.To > from)) ?? false);
        var trailing = ordered.Count == 0 || trailingInterval.HasValue && to - ordered[^1].EffectiveAt > TimeSpan.FromMilliseconds(trailingInterval.Value * 1.5)
            || (gaps?.Any(g => g.From < to && (g.To == null || g.To >= to)) ?? false);
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
                var coverage = protocol ? SamplingCoverage.Calculate(coveredFrom, coveredTo, values.Length, profiles ?? [])
                    : new SamplingCoverageResult("Periodic", expected, Math.Min(1, values.Length / expected));
                if (protocol)
                {
                    hasGap = coverage.Mode == "Unknown" || coverage.CoverageRatio < 1 || rows.Any(x => !SamplePolicy.IsGood(x))
                        || samples.Any(x => x.EffectiveAt >= start && x.EffectiveAt < start.AddMinutes(1) && x.GapBefore);
                }
                buckets.Add(new(start, values.Length == 0 ? null : values.Min(), values.Length == 0 ? null : values.Max(),
                    values.Length == 0 ? null : values.Average(), values.Length, rows.Count(x => x.Quality == "Uncertain"),
                    rows.Count(x => x.Quality == "Bad"), rows.Count(x => x.TimeAnomaly), hasGap, coverage.CoverageRatio, coverage.Mode));
            }
        }
        return new(point.Id, point.CanonicalUnit, from, to, bucket, checkedAt,
            bucket == "raw" ? samples : [], buckets, leading, trailing);
    }

    private static DateTimeOffset FloorMinute(DateTimeOffset at) => new(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0, TimeSpan.Zero);
}

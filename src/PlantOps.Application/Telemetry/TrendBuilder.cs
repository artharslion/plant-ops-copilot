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
        var pointProfiles = profiles?.Where(x => x.PointId == point.Id).ToArray() ?? [];
        bool TimingGap(DateTimeOffset start, DateTimeOffset end)
        {
            return protocol ? SamplingCoverage.HasIntervalGap(start, end, pointProfiles)
                : end - start > TimeSpan.FromMilliseconds(point.ExpectedIntervalMs * 1.5);
        }
        bool ExplicitGap(DateTimeOffset start, DateTimeOffset end, bool includeEnd = false) =>
            (end > start || includeEnd) && (gaps?.Any(g => g.PointId == point.Id
                && (includeEnd ? g.From <= end : g.From < end) && (g.To == null || g.To > start)) ?? false);
        var samples = new List<TrendSample>();
        DateTimeOffset prior = from;
        foreach (var r in ordered)
        {
            samples.Add(new(r.Id, r.EffectiveAt, r.ReceivedAt, r.NumericValue, r.Quality, r.TimeAnomaly,
                SamplePolicy.HasOverflow(r.RawStatusCode) || TimingGap(prior, r.EffectiveAt)
                    || ExplicitGap(prior, r.EffectiveAt, includeEnd: true), r.DataSourceId, r.SourceMappingId, r.MappingVersion, r.SourceKind,
                r.TimestampOrigin, r.SourceTimestamp, r.ServerTimestamp, r.RawStatusCode, r.StatusSymbol));
            prior = r.EffectiveAt;
        }
        var leading = ordered.Count == 0 || TimingGap(from, ordered[0].EffectiveAt) || ExplicitGap(from, ordered[0].EffectiveAt);
        var trailing = ordered.Count == 0 || TimingGap(ordered[^1].EffectiveAt, to) || ExplicitGap(ordered[^1].EffectiveAt, to);
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
                var coverage = protocol ? SamplingCoverage.Calculate(coveredFrom, coveredTo, values.Length, pointProfiles)
                    : new SamplingCoverageResult("Periodic", expected, Math.Min(1, values.Length / expected));
                if (protocol)
                {
                    hasGap = coverage.Mode == "Unknown" || coverage.CoverageRatio < 1 || rows.Any(x => !SamplePolicy.IsGood(x))
                        || SamplingCoverage.HasTimingGap(coveredFrom, coveredTo, pointProfiles, rows)
                        || (gaps?.Any(g => g.PointId == point.Id && g.From < coveredTo
                            && (g.To == null || g.To > coveredFrom)) ?? false);
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

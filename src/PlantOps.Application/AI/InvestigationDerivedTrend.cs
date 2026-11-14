using PlantOps.Application.Telemetry;
using PlantOps.Domain.Telemetry;

namespace PlantOps.Application.AI;

public sealed record InvestigationTrendChange(Guid FirstReadingId, Guid LastReadingId, DateTimeOffset From,
    DateTimeOffset To, double FirstValue, double LastValue, double Change, double RatePerMinute, int SampleCount, int IncreaseCount);

public sealed record InvestigationTrendSegment(Guid FirstReadingId, Guid LastReadingId, DateTimeOffset From,
    DateTimeOffset To, double Min, double Max, int SampleCount);

public sealed record InvestigationTrendGap(DateTimeOffset From, DateTimeOffset? To, string Reason);

public sealed record InvestigationTrendProfile(Guid Id, DateTimeOffset From, DateTimeOffset? To,
    Guid MappingId, long MappingVersion, string Mode, double? IntervalMs);

public sealed record InvestigationDerivedTrend(string AlgorithmVersion, Guid PointId, string Code, string Unit,
    Guid AlarmId, string WindowKind, DateTimeOffset From, DateTimeOffset To, DateTimeOffset AsOf, DateTimeOffset RetrievedAt,
    InvestigationObservedReading[] Samples, InvestigationTrendGap[] Gaps, InvestigationTrendProfile[] Profiles,
    InvestigationTrendChange? Change, InvestigationTrendChange[] RiseSegments, string[] Reasons, bool IsAlarmPoint = true,
    InvestigationTrendSegment? LatestSegment = null);

// Only ordered, version-confirmed observations support a change. A rising
// observation sequence does not establish continuous physical heating or cause.
public static class InvestigationTrendPolicy
{
    public const string Version = "ordered-raw-change-v2";
    public const int MaxSamples = 601;

    public static InvestigationDerivedTrend Calculate(InvestigationPointSnapshot point, Guid alarmId, string kind,
        DateTimeOffset from, DateTimeOffset to, DateTimeOffset retrievedAt, IReadOnlyList<MeasurementReading> readings,
        IReadOnlyList<IngestionGap> gaps, IReadOnlyList<SamplingProfile> profiles, int expectedIntervalMs, bool truncated = false)
    {
        if (from >= to || to > point.AsOf || to - from > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentException("A raw investigation window must end by asOf and span at most five minutes.");
        }
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var rows = readings.OrderBy(x => x.EffectiveAt).ThenBy(x => x.ReceivedAt).ThenBy(x => x.Id).Take(MaxSamples).ToArray();
        var duplicateTimes = rows.GroupBy(x => x.EffectiveAt).Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet();
        var relevantGaps = gaps.Where(x => x.PointId == point.PointId && x.From < to && (x.To == null || x.To > from)).ToArray();
        var relevantProfiles = profiles.Where(x => x.PointId == point.PointId && x.SourceMappingId == point.MappingId
            && x.MappingVersion == point.MappingVersion && x.EffectiveFrom < to && (x.EffectiveTo == null || x.EffectiveTo > from)).ToArray();
        if (truncated || readings.Count > MaxSamples)
        {
            reasons.Add("Truncated");
        }
        bool Valid(MeasurementReading row)
        {
            var valid = true;
            void Reject(string reason)
            {
                reasons.Add(reason);
                valid = false;
            }
            if (!SamplePolicy.IsGood(row))
            {
                Reject("QualityOrTimeUntrusted");
            }
            if (duplicateTimes.Contains(row.EffectiveAt))
            {
                Reject("DuplicateObservationTime");
            }
            if (row.TimestampOrigin is not ("Source" or "Server") || row.EffectiveAt < from || row.EffectiveAt >= to
                || row.ReceivedAt > point.AsOf || row.EffectiveAt > point.AsOf
                || !SamplePolicy.IsFresh(row.EffectiveAt, row.ReceivedAt, point.StaleAfterMs))
            {
                Reject("ObservationTimeUnverified");
            }
            if (row.PointId != point.PointId || row.CanonicalUnit != point.Unit || row.DataSourceId != point.SourceId
                || row.SourceMappingId != point.MappingId || row.MappingVersion != point.MappingVersion
                || row.SourceKind != point.SourceKind || !row.SourceVersion.HasValue || row.SourceVersion != point.SourceVersion
                || !row.PointVersion.HasValue || row.PointVersion != point.PointVersion)
            {
                Reject("ConfigurationVersionUnverified");
            }
            return valid;
        }
        bool Connected(MeasurementReading first, MeasurementReading last)
        {
            string? reason = null;
            if (last.EffectiveAt <= first.EffectiveAt)
            {
                reason = "DuplicateObservationTime";
            }
            else if (relevantGaps.Any(x => x.From <= last.EffectiveAt && (x.To == null || x.To > first.EffectiveAt)))
            {
                reason = "ExplicitGap";
            }
            else if (point.SourceKind == "OpcUa")
            {
                // Profile timestamps describe reception/configuration, not physical sampling.
                var matches = relevantProfiles.Where(x => x.EffectiveFrom <= first.ReceivedAt
                    && (x.EffectiveTo == null || x.EffectiveTo > last.ReceivedAt)).ToArray();
                if (matches.Length != 1 || matches[0].Mode != "Periodic" || matches[0].RevisedSamplingIntervalMs is not > 0)
                {
                    reason = "SamplingContractUnverified";
                }
                else if ((last.EffectiveAt - first.EffectiveAt).TotalMilliseconds > matches[0].RevisedSamplingIntervalMs * 1.5)
                {
                    reason = "IntervalGap";
                }
            }
            else if ((last.EffectiveAt - first.EffectiveAt).TotalMilliseconds > expectedIntervalMs * 1.5)
            {
                reason = "IntervalGap";
            }
            if (reason != null)
            {
                reasons.Add(reason);
            }
            return reason == null;
        }
        InvestigationTrendChange? Change(IReadOnlyList<MeasurementReading> chain)
        {
            if (chain.Count < 2 || chain[^1].EffectiveAt <= chain[0].EffectiveAt)
            {
                return null;
            }
            var delta = chain[^1].NumericValue!.Value - chain[0].NumericValue!.Value;
            var rate = delta / (chain[^1].EffectiveAt - chain[0].EffectiveAt).TotalMinutes;
            if (!double.IsFinite(delta) || !double.IsFinite(rate))
            {
                reasons.Add("ArithmeticUnavailable");
                return null;
            }
            return new(chain[0].Id, chain[^1].Id, chain[0].EffectiveAt, chain[^1].EffectiveAt,
                chain[0].NumericValue!.Value, chain[^1].NumericValue!.Value, delta, rate, chain.Count,
                chain.Zip(chain.Skip(1)).Count(x => x.Second.NumericValue > x.First.NumericValue));
        }
        var chains = new List<List<MeasurementReading>>();
        List<MeasurementReading>? current = null;
        foreach (var row in rows)
        {
            if (!Valid(row))
            {
                current = null;
                continue;
            }
            if (current == null || !Connected(current[^1], row))
            {
                current = [];
                chains.Add(current);
            }
            current.Add(row);
        }
        InvestigationTrendChange? whole = null;
        if (!reasons.Contains("Truncated") && chains.Count == 1 && chains[0].Count == rows.Length)
        {
            whole = Change(chains[0]);
        }
        if (rows.Length < 2)
        {
            reasons.Add("InsufficientSamples");
        }
        var rises = new List<InvestigationTrendChange>();
        foreach (var chain in chains)
        {
            var start = 0;
            for (var i = 1; i <= chain.Count; i++)
            {
                if (i < chain.Count && chain[i].NumericValue >= chain[i - 1].NumericValue)
                {
                    continue;
                }
                var rise = Change(chain.GetRange(start, i - start));
                if (rise?.IncreaseCount >= 2)
                {
                    rises.Add(rise);
                }
                start = i;
            }
        }
        // A truncated query must not imply the omitted tail was analyzed.
        var selectedRises = reasons.Contains("Truncated") ? [] : rises.OrderByDescending(x => x.Change).ThenBy(x => x.From).Take(3).ToArray();
        if (rises.Count > 3)
        {
            reasons.Add("RiseSegmentsOmitted");
        }
        InvestigationTrendSegment? latest = null;
        // A bounded tail must end at the actual last row and never cross a rejected pair.
        if (whole == null && !reasons.Contains("Truncated") && chains.LastOrDefault() is { Count: >= 2 } tail && tail[^1] == rows[^1])
        {
            latest = new(tail[0].Id, tail[^1].Id, tail[0].EffectiveAt, tail[^1].EffectiveAt,
                tail.Min(x => x.NumericValue!.Value), tail.Max(x => x.NumericValue!.Value), tail.Count);
        }
        return new(Version, point.PointId, point.Code, point.Unit, alarmId, kind, from, to, point.AsOf, retrievedAt,
            rows.Select(x => new InvestigationObservedReading(x.Id, x.EffectiveAt, x.ReceivedAt,
                x.NumericValue.HasValue && double.IsFinite(x.NumericValue.Value) ? x.NumericValue : null,
                x.Quality, x.TimeAnomaly, x.DataSourceId, x.SourceMappingId, x.MappingVersion, x.SourceKind,
                x.TimestampOrigin, x.SourceTimestamp, x.ServerTimestamp, x.RawStatusCode, x.StatusSymbol, x.SourceVersion, x.PointVersion)).ToArray(),
            relevantGaps.Select(x => new InvestigationTrendGap(x.From, x.To, x.Reason)).ToArray(),
            relevantProfiles.Select(x => new InvestigationTrendProfile(x.Id, x.EffectiveFrom, x.EffectiveTo,
                x.SourceMappingId, x.MappingVersion, x.Mode, x.RevisedSamplingIntervalMs)).ToArray(),
            whole, selectedRises, reasons.Order(StringComparer.Ordinal).ToArray(), LatestSegment: latest);
    }
}

using PlantOps.Domain.Telemetry;

namespace PlantOps.Application.Telemetry;

public sealed record SamplingCoverageResult(string Mode, double? ExpectedCount, double? CoverageRatio);

public static class SamplingCoverage
{
    // Adjacent observations bound an otherwise empty window; revision boundaries reset the tolerance.
    public static bool HasIntervalGap(DateTimeOffset from, DateTimeOffset to, IReadOnlyList<SamplingProfile> profiles)
    {
        if (to <= from)
        {
            return false;
        }
        var relevant = profiles.Where(x => x.EffectiveFrom < to && (x.EffectiveTo == null || x.EffectiveTo > from)).ToArray();
        var boundaries = relevant.Select(x => x.EffectiveFrom).Concat(relevant.Where(x => x.EffectiveTo.HasValue)
            .Select(x => x.EffectiveTo!.Value)).Where(x => x > from && x < to).Append(from).Append(to).Distinct().Order().ToArray();
        foreach (var (start, end) in boundaries.Zip(boundaries.Skip(1)))
        {
            var matching = relevant.Where(x => x.EffectiveFrom <= start && (x.EffectiveTo == null || x.EffectiveTo >= end)).ToArray();
            if (matching.Length == 1 && matching[0].Mode == "Periodic"
                && matching[0].RevisedSamplingIntervalMs is { } interval && double.IsFinite(interval) && interval > 0
                && (end - start).TotalMilliseconds > interval * 1.5)
            {
                return true;
            }
        }
        return false;
    }

    public static bool HasTimingGap(DateTimeOffset from, DateTimeOffset to, IReadOnlyList<SamplingProfile> profiles,
        IReadOnlyList<MeasurementReading> readings)
    {
        foreach (var profile in profiles.Where(x => x.Mode == "Periodic" && x.EffectiveFrom < to
            && (x.EffectiveTo == null || x.EffectiveTo > from)))
        {
            if (profile.RevisedSamplingIntervalMs is not { } interval || !double.IsFinite(interval) || interval <= 0)
            {
                return true;
            }
            var start = profile.EffectiveFrom > from ? profile.EffectiveFrom : from;
            var end = profile.EffectiveTo is { } until && until < to ? until : to;
            var times = readings.Where(x => SamplePolicy.IsGood(x) && x.EffectiveAt >= start && x.EffectiveAt < end)
                .Select(x => x.EffectiveAt).Distinct().Order().ToArray();
            var tolerance = TimeSpan.FromMilliseconds(interval * 1.5);
            if (times.Length == 0 || times[0] - start > tolerance || end - times[^1] > tolerance
                || times.Zip(times.Skip(1)).Any(pair => pair.Second - pair.First > tolerance))
            {
                return true;
            }
        }
        return false;
    }

    public static SamplingCoverageResult Calculate(DateTimeOffset from, DateTimeOffset to, int goodCount,
        IReadOnlyList<SamplingProfile> profiles)
    {
        if (to <= from || goodCount < 0)
        {
            throw new ArgumentException("Invalid sampling coverage window.");
        }
        var relevant = profiles.Where(x => x.EffectiveFrom < to && (x.EffectiveTo == null || x.EffectiveTo > from))
            .OrderBy(x => x.EffectiveFrom).ToArray();
        var cursor = from;
        var expected = 0.0;
        foreach (var profile in relevant)
        {
            var start = profile.EffectiveFrom > from ? profile.EffectiveFrom : from;
            var end = profile.EffectiveTo is { } until && until < to ? until : to;
            if (start != cursor || end <= start || profile.Mode != "Periodic"
                || profile.RevisedSamplingIntervalMs is not { } interval || !double.IsFinite(interval) || interval <= 0)
            {
                return new(relevant.Any(x => x.Mode == "Changes") ? "Changes" : "Unknown", null, null);
            }
            expected += (end - start).TotalMilliseconds / interval;
            cursor = end;
        }
        if (cursor != to || expected <= 0)
        {
            return new("Unknown", null, null);
        }
        return new("Periodic", expected, Math.Min(1, goodCount / expected));
    }
}

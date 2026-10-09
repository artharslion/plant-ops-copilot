using PlantOps.Domain.Telemetry;

namespace PlantOps.Application.Telemetry;

// These values must come from an observed revision, never a fallback to requested configuration.
// This offline contract cannot establish their protocol provenance or persist a lifecycle barrier.
public sealed record SamplingRevision(Guid PointId, Guid SourceMappingId, long MappingVersion,
    DateTimeOffset EffectiveFrom, string Mode, double RevisedSamplingIntervalMs,
    double RevisedPublishingIntervalMs, long RevisedQueueSize);

public static class SamplingProfileTransitions
{
    public static IReadOnlyList<SamplingProfile> Activate(IReadOnlyList<SamplingProfile> history, SamplingRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        if (revision.SourceMappingId == Guid.Empty || revision.MappingVersion < 1
            || revision.Mode is not ("Periodic" or "Changes")
            || !ValidSampling(revision.Mode, revision.RevisedSamplingIntervalMs)
            || !ValidPublishing(revision.RevisedPublishingIntervalMs)
            || revision.RevisedQueueSize is < 1 or > 10000)
        {
            throw new ArgumentException("Invalid observed sampling revision.");
        }
        var result = CopyValidatedHistory(history, revision.PointId);
        if (result.Any(x => x.SourceMappingId == revision.SourceMappingId && x.MappingVersion > revision.MappingVersion))
        {
            throw new ArgumentException("A sampling revision cannot roll back its mapping version.");
        }
        if (result.Count > 0)
        {
            var last = result[^1];
            if (last.EffectiveTo is { } end)
            {
                if (revision.EffectiveFrom < end)
                {
                    throw new ArgumentException("A sampling revision cannot overwrite closed history.");
                }
            }
            else
            {
                CloseProfile(last, revision.EffectiveFrom);
            }
        }
        result.Add(new()
        {
            PointId = revision.PointId,
            SourceMappingId = revision.SourceMappingId,
            MappingVersion = revision.MappingVersion,
            EffectiveFrom = revision.EffectiveFrom,
            Mode = revision.Mode,
            RevisedSamplingIntervalMs = revision.RevisedSamplingIntervalMs,
            RevisedPublishingIntervalMs = revision.RevisedPublishingIntervalMs,
            RevisedQueueSize = revision.RevisedQueueSize
        });
        return result;
    }

    public static IReadOnlyList<SamplingProfile> Close(IReadOnlyList<SamplingProfile> history, Guid pointId,
        DateTimeOffset effectiveTo)
    {
        var result = CopyValidatedHistory(history, pointId);
        if (result.Count == 0 || result[^1].EffectiveTo != null)
        {
            throw new ArgumentException("No open sampling interval to close.");
        }
        CloseProfile(result[^1], effectiveTo);
        return result;
    }

    private static void CloseProfile(SamplingProfile profile, DateTimeOffset effectiveTo)
    {
        if (effectiveTo <= profile.EffectiveFrom)
        {
            throw new ArgumentException("A sampling interval must have a positive duration.");
        }
        profile.EffectiveTo = effectiveTo;
    }

    private static List<SamplingProfile> CopyValidatedHistory(IReadOnlyList<SamplingProfile> history, Guid pointId)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (pointId == Guid.Empty || history.Any(x => x.Id == Guid.Empty || x.PointId != pointId
            || x.SourceMappingId == Guid.Empty || x.MappingVersion < 1
            || x.EffectiveTo <= x.EffectiveFrom || x.Mode is not ("Periodic" or "Changes" or "Unknown")
            || !ValidSampling(x.Mode, x.RevisedSamplingIntervalMs)
            || (x.RevisedPublishingIntervalMs is { } publishing && !ValidPublishing(publishing))
            || x.RevisedQueueSize is < 1 or > 10000)
            || history.Select(x => x.Id).Distinct().Count() != history.Count)
        {
            throw new ArgumentException("Invalid sampling history identity, interval or parameters.");
        }
        var ordered = history.OrderBy(x => x.EffectiveFrom).ToArray();
        var versions = new Dictionary<Guid, long>();
        for (var i = 0; i < ordered.Length; i++)
        {
            var profile = ordered[i];
            if ((i > 0 && (ordered[i - 1].EffectiveTo == null || ordered[i - 1].EffectiveTo > profile.EffectiveFrom))
                || (versions.TryGetValue(profile.SourceMappingId, out var version) && version > profile.MappingVersion))
            {
                throw new ArgumentException("Sampling history overlaps or rolls back a mapping version.");
            }
            versions[profile.SourceMappingId] = profile.MappingVersion;
        }
        return ordered.Select(x => new SamplingProfile
        {
            Id = x.Id,
            PointId = x.PointId,
            SourceMappingId = x.SourceMappingId,
            MappingVersion = x.MappingVersion,
            EffectiveFrom = x.EffectiveFrom,
            EffectiveTo = x.EffectiveTo,
            Mode = x.Mode,
            RevisedSamplingIntervalMs = x.RevisedSamplingIntervalMs,
            RevisedPublishingIntervalMs = x.RevisedPublishingIntervalMs,
            RevisedQueueSize = x.RevisedQueueSize
        }).ToList();
    }

    private static bool ValidSampling(string mode, double? interval) =>
        interval is { } value ? double.IsFinite(value) && (mode == "Periodic" ? value > 0 : value >= 0) : mode != "Periodic";

    private static bool ValidPublishing(double interval) => double.IsFinite(interval) && interval > 0;
}

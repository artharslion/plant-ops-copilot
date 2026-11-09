using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PlantOps.Application.AI;

namespace PlantOps.AI;

// Text is optional context; typed point/trend metadata is supplied separately by the runner.
internal static class InvestigationEvidenceProjection
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal sealed record Item(string Id, string Kind, string[] Facts, int OmittedFactCount, InvestigationEvidenceState? State);

    public static Item[] Pack(IReadOnlyList<InvestigationEvidence> evidence, int maximumBytes)
    {
        var latestPoints = evidence.Where(x => x.PointStatus != null).GroupBy(x => x.ResourceId)
            .Select(x => x.Last().Id).ToHashSet(StringComparer.Ordinal);
        var items = evidence.Where(x => x.PointStatus == null || latestPoints.Contains(x.Id)).OrderBy(x => x.Kind switch
        {
            "Alarm" => 0,
            "PointStatus" => 1,
            "Asset" => 2,
            "Document" => 3,
            "EventDerivedTrend" or "CurrentDerivedTrend" => 4,
            "RelatedComparison" => 5,
            _ => 6
        }).ToArray();
        var result = items.Select(x =>
        {
            // The selected alarm snapshot carries the trigger, lifecycle and historical rule.
            // The service puts the recovery-anchor excerpt (or availability limitation) first.
            var facts = x.Kind switch
            {
                "Alarm" => x.Facts,
                "RecoveryReadings" or "Asset" or "DataQuality" => x.Facts.Take(1).ToArray(),
                _ => []
            };
            return new Item(x.Id, x.Kind, facts, x.Facts.Length - facts.Length, x.State);
        }).ToArray();
        var candidates = items.SelectMany((item, index) => item.Facts.Select((fact, round) => new { index, fact, round }))
            .OrderBy(x => x.round).ThenBy(x => x.index);
        foreach (var candidate in candidates)
        {
            var before = result[candidate.index];
            if (before.Facts.Contains(candidate.fact, StringComparer.Ordinal))
            {
                continue;
            }

            result[candidate.index] = before with
            {
                Facts = [.. before.Facts, candidate.fact],
                OmittedFactCount = before.OmittedFactCount - 1
            };
            if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result, Json)) > maximumBytes)
            {
                result[candidate.index] = before;
            }
        }
        return result;
    }
}

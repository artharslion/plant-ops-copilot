using System.Text.Json;
using PlantOps.Application.AI;

namespace PlantOps.Tests;

// Acceptance evidence only: never changes the engine's tool choices or advice.
internal static class AgentEvidenceAcceptance
{
    internal static string[] Check(IReadOnlyList<LlmToolCall> choices, IReadOnlyList<JsonElement> traces,
        IReadOnlyList<InvestigationEvidence> evidence, InvestigationAdvice? advice)
    {
        var failures = new List<string>();
        if (choices.Count == 0)
        {
            failures.Add("AutonomousEvidenceNotDemonstrated");
        }

        var refs = advice == null ? [] : advice.Findings.SelectMany(x => x.EvidenceRefs)
            .Concat(advice.Recommendations.SelectMany(x => x.EvidenceRefs))
            .Concat(advice.Hypotheses.SelectMany(x => x.SupportingEvidenceRefs.Concat(x.ContradictingEvidenceRefs)))
            .Concat(advice.Gaps.SelectMany(x => x.EvidenceRefs)).ToHashSet(StringComparer.Ordinal);
        var cited = false;
        var hasUsableEvidence = false;
        var remainingTraces = traces.ToList();
        foreach (var choice in choices)
        {
            JsonElement args;
            try
            {
                args = JsonSerializer.Deserialize<JsonElement>(choice.Arguments);
                InvestigationToolPolicy.Validate(choice.Name, args);
            }
            catch (Exception e) when (e is JsonException or LlmException)
            {
                failures.Add("InvalidToolArguments");
                continue;
            }
            var index = remainingTraces.FindIndex(x => x.GetProperty("name").GetString() == choice.Name
                && x.TryGetProperty("arguments", out var actual) && Canonical(actual) == Canonical(args));
            if (index < 0)
            {
                failures.Add("ToolTraceMissing");
                continue;
            }
            var trace = remainingTraces[index];
            remainingTraces.RemoveAt(index);
            if (trace.GetProperty("outcome").GetString() != "Succeeded")
            {
                failures.Add("ToolReadNotSucceeded");
                continue;
            }
            foreach (var item in trace.GetProperty("evidence").EnumerateArray())
            {
                var frozen = evidence.SingleOrDefault(e => e.Id == item.GetProperty("id").GetString());
                if (frozen == null || frozen.Sha256 != item.GetProperty("sha256").GetString())
                {
                    failures.Add("ToolEvidenceMismatch");
                    continue;
                }
                if (frozen.State?.Availability == "Unavailable")
                {
                    failures.Add("ToolReadNotSucceeded");
                }
                if (frozen.State?.Availability is "Available" or "Partial" && frozen.Kind != "DataQuality" && frozen.Facts.Length > 0)
                {
                    hasUsableEvidence = true;
                    cited |= refs.Contains(frozen.Id);
                }
            }
        }
        if (choices.Count > 0 && !hasUsableEvidence)
        {
            failures.Add("NoUsableToolEvidence");
        }
        if (advice == null)
        {
            failures.Add("NoValidAdvice");
        }

        if (choices.Count > 0 && !cited)
        {
            failures.Add("ToolEvidenceNotCited");
        }

        return failures.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string Canonical(JsonElement value) => JsonSerializer.Serialize(value.EnumerateObject()
        .OrderBy(x => x.Name, StringComparer.Ordinal).ToDictionary(x => x.Name, x => x.Value));

    internal static LlmToolCall[] CapturedChoices(string ledger, JsonElement record) =>
        Directory.GetFiles(Path.GetDirectoryName(ledger)!, Path.GetFileName(ledger) + ".completion-*.json")
            .Select(File.ReadAllText).Select(x => JsonSerializer.Deserialize<JsonElement>(x))
            .Where(x => record.GetProperty("requests").EnumerateArray().Any(r => string.Equals(
                r.GetProperty("requestSha256").GetString(), x.GetProperty("requestSha256").GetString(), StringComparison.OrdinalIgnoreCase)))
            .SelectMany(x => x.GetProperty("completion").Deserialize<LlmCompletion>()!.ToolCalls).ToArray();
}

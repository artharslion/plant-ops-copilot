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
            var matching = traces.Where(x => x.GetProperty("name").GetString() == choice.Name
                && x.TryGetProperty("arguments", out var actual) && Canonical(actual) == Canonical(args)).ToArray();
            var successful = matching.Where(x => x.GetProperty("outcome").GetString() == "Succeeded").ToArray();
            if (successful.Length == 0)
            {
                failures.Add("ToolReadNotSucceeded");
            }

            var usable = successful.SelectMany(x => x.GetProperty("evidence").EnumerateArray())
                .Where(x => evidence.Any(e => e.Id == x.GetProperty("id").GetString()
                    && e.Sha256 == x.GetProperty("sha256").GetString()
                    && e.State?.Availability is "Available" or "Partial" && e.Kind != "DataQuality" && e.Facts.Length > 0))
                .Select(x => x.GetProperty("id").GetString()!).ToArray();
            if (usable.Length == 0)
            {
                failures.Add("NoUsableToolEvidence");
            }

            cited |= usable.Any(refs.Contains);
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

using System.Text.Json;
using Microsoft.Extensions.Options;
using PlantOps.AI;
using PlantOps.Application.AI;
using Xunit;

namespace PlantOps.Tests;

public sealed class P61EvidenceReplayTests
{
    [EvidenceReplayTheory]
    [InlineData("p61-real-20261029", "counter-v29/all-unverifiable.json", "不得写入或控制设备")]
    [InlineData("p61-real-20261030", "baseline-v30-ready/partial-bad-high-vibration.json", "不能确认设备安全")]
    public async Task OriginalRejectedAnswerPassesNarrowRepairWithoutAnotherModelCall(string directory, string reportPath, string phrase)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("PLANTOPS_REPLAY_ROOT")!, directory);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, reportPath)));
        var run = report.RootElement.GetProperty("Run").Deserialize<InvestigationRunDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var hash = run.RunRecord!.Value.GetProperty("requests").EnumerateArray().Last().GetProperty("requestSha256").GetString();
        LlmCompletion? completion = null;
        foreach (var path in Directory.EnumerateFiles(root, "requests.jsonl.completion-*.json"))
        {
            using var file = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            if (string.Equals(hash, file.RootElement.GetProperty("requestSha256").GetString(), StringComparison.OrdinalIgnoreCase))
            {
                completion = file.RootElement.GetProperty("completion").Deserialize<LlmCompletion>();
            }
        }
        Assert.NotNull(completion);
        Assert.Contains(phrase, completion.Content!);
        // Replay the captured answer against the complete original immutable ledger.
        // This gateway is deliberately Fake and never creates an HTTP client.
        var gateway = new FakeLlmGateway([completion]);
        var engine = new LegacyInvestigationEngine(gateway, Options.Create(new AiOptions { ServerRenderedFacts = true }));
        using var budget = new LlmBudget { EvidenceAsOf = run.Assessment!.AsOf };
        var result = await engine.ExecuteAsync(run.Evidence,
            (_, _, _) => throw new InvalidOperationException("The frozen final answer requires no new tool reads."),
            _ => Task.CompletedTask, budget, default);
        Assert.NotNull(result.ResultJson);
    }
}

public sealed class EvidenceReplayTheoryAttribute : TheoryAttribute
{
    public EvidenceReplayTheoryAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PLANTOPS_REPLAY_ROOT")))
        {
            Skip = "Explicit local evidence replay only; no external calls.";
        }
    }
}

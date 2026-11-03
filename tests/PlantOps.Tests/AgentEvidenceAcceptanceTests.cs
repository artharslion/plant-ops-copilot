using System.Text.Json;
using Microsoft.Extensions.Options;
using PlantOps.AI;
using PlantOps.Application.AI;
using Xunit;

namespace PlantOps.Tests;

public sealed class AgentEvidenceAcceptanceTests
{
    [Theory]
    [InlineData("read-and-cite", null)]
    [InlineData("some-empty", null)]
    [InlineData("typed-empty", null)]
    [InlineData("mixed-failure", "ToolReadNotSucceeded")]
    [InlineData("forged-hash", "ToolEvidenceMismatch")]
    [InlineData("foreign-evidence", "ToolEvidenceMismatch")]
    [InlineData("missing-trace", "ToolTraceMissing")]
    [InlineData("repeated-choice", "ToolTraceMissing")]
    [InlineData("no-read", "AutonomousEvidenceNotDemonstrated")]
    [InlineData("empty", "NoUsableToolEvidence")]
    [InlineData("unavailable", "ToolReadNotSucceeded")]
    [InlineData("uncited", "ToolEvidenceNotCited")]
    public async Task ScriptedEngineCannotSubstituteRoundsOrValidAdviceForAutonomousEvidence(string scenario, string? expected)
    {
        var point = Guid.NewGuid();
        var initial = new InvestigationEvidence("E2", "PointStatus", "Point", ["Frozen observation"], "initial",
            DateTimeOffset.UtcNow, point, PointStatus: new()
            {
                PointId = point,
                CurrentTrust = "Trusted",
                CurrentAnomaly = "Present"
            },
            State: new("Available", "Trusted", "Present"));
        var document = new InvestigationEvidence("D2", "Document", "Authorized procedure", ["Record independent observations"], "tool",
            DateTimeOffset.UtcNow, Guid.NewGuid(), State: new(scenario == "unavailable" ? "Unavailable" : "Available"));
        var advice = InvestigationAdviceTests.Valid(point.ToString());
        if (scenario is "read-and-cite" or "some-empty" or "typed-empty" or "mixed-failure" or "forged-hash" or "foreign-evidence" or "missing-trace" or "repeated-choice")
        {
            advice = advice with
            {
                Recommendations = [advice.Recommendations[0] with { EvidenceRefs = ["E2", "D2"] }]
            };
        }
        // Prose claiming a read cannot prove one.
        if (scenario == "no-read")
        {
            advice = advice with
            {
                Summary = "I independently inspected the authorized tools."
            };
        }

        LlmToolCall[] choices = scenario == "no-read" ? [] : [new("read", "search_documents", """{"query":"procedure","limit":1}""")];
        if (scenario is "some-empty" or "typed-empty" or "mixed-failure")
        {
            choices = [.. choices, new("empty", "get_maintenance_history", "{}")];
        }
        var final = new LlmCompletion(InvestigationAdviceContract.Serialize(advice), [], "script", new(100, 50));
        var gateway = new ScriptedLlmGateway(choices.Length == 0 ? [final] : [new(null, choices, "script", new(100, 20)), final]);
        using var budget = new LlmBudget(new(MaxInputTokens: 32000, MaxOutputTokens: 6000));
        var traces = new List<JsonElement>();
        if (scenario == "no-read")
        {
            traces.Add(JsonSerializer.SerializeToElement(new
            {
                name = "get_asset_details",
                arguments = new
                {
                },
                outcome = "Succeeded",
                evidence = new[] { new { id = initial.Id, sha256 = initial.Sha256 } }
            }));
        }
        var frozen = new List<InvestigationEvidence> { initial };
        var result = await new InvestigationEngine(gateway, Options.Create(new AiOptions { Provider = "OpenAiCompatible" }))
            .ExecuteAsync(frozen.ToArray(), (name, args, _) =>
            {
                InvestigationToolPolicy.Validate(name, args);
                var maintenance = name == "get_maintenance_history";
                InvestigationEvidence[] returned = scenario == "empty" || maintenance ? [] : [document];
                if (maintenance && scenario == "typed-empty")
                {
                    returned = [document with { Id = "M1", Kind = "Maintenance", State = new("Empty") }];
                }
                frozen.AddRange(returned);
                traces.Add(JsonSerializer.SerializeToElement(new
                {
                    name,
                    arguments = args,
                    outcome = scenario == "unavailable" || maintenance && scenario == "mixed-failure" ? "Unavailable" : "Succeeded",
                    evidence = returned.Select(x => new { id = scenario == "foreign-evidence" ? "FORGED" : x.Id, sha256 = scenario == "forged-hash" ? "forged" : x.Sha256 })
                }));
                return Task.FromResult(new InvestigationToolResult(returned, []));
            }, _ => Task.CompletedTask, budget, CancellationToken.None);
        var parsed = InvestigationAdviceContract.ParseAndValidate(result.ResultJson, frozen);
        if (scenario == "missing-trace")
        {
            traces.Clear();
        }
        if (scenario == "repeated-choice")
        {
            choices = [.. choices, choices[0] with { Id = "second-read" }];
        }
        var failures = AgentEvidenceAcceptance.Check(choices, traces, frozen, parsed);
        if (expected == null)
        {
            Assert.Empty(failures);
        }
        else
        {
            Assert.Contains(expected, failures);
        }

        Assert.False(result.Simulated);
        if (scenario == "no-read")
        {
            Assert.Equal(1, budget.ModelRounds);
            Assert.Equal(0, budget.ToolCalls); // Valid Direct Finish, failed autonomous acceptance.
            Assert.Equal("DraftReady", parsed.WorkOrder.Status);
        }
    }

    [Fact]
    public void CapturesAreScopedToTheRunsRequestHashesAndNotOtherPaidRuns()
    {
        var root = Path.Combine(Path.GetTempPath(), "p6-capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var completion = new LlmCompletion(null, [new("read", "get_recent_alarms", "{}")], "script", new(100, 20));
            File.WriteAllText(Path.Combine(root, "requests.jsonl.completion-current.json"), JsonSerializer.Serialize(new
            {
                requestSha256 = "ABC",
                completion
            }));
            File.WriteAllText(Path.Combine(root, "requests.jsonl.completion-other.json"), JsonSerializer.Serialize(new
            {
                requestSha256 = "DEF",
                completion
            }));
            var record = JsonSerializer.SerializeToElement(new
            {
                requests = new[] { new { requestSha256 = "abc" } }
            });
            Assert.Single(AgentEvidenceAcceptance.CapturedChoices(Path.Combine(root, "requests.jsonl"), record));
            Assert.Empty(AgentEvidenceAcceptance.CapturedChoices(Path.Combine(root, "requests.jsonl"),
                JsonSerializer.SerializeToElement(new
                {
                    requests = Array.Empty<object>()
                })));
        }
        finally
        {
            // Delete only the exact files this test created, never a recursive computed path.
            File.Delete(Path.Combine(root, "requests.jsonl.completion-current.json"));
            File.Delete(Path.Combine(root, "requests.jsonl.completion-other.json"));
            Directory.Delete(root);
        }
    }

    [Theory]
    [InlineData("invalid-arguments")]
    [InlineData("budget")]
    [InlineData("cancelled")]
    [InlineData("invalid-result")]
    public async Task ScriptedFailureCannotBeAcceptedAsAnInvestigation(string scenario)
    {
        var point = Guid.NewGuid();
        InvestigationEvidence[] evidence = [new("E2", "Alarm", "Fixture", ["Alarm snapshot"], "h", DateTimeOffset.UtcNow, point)];
        using var budget = new LlmBudget(new(MaxInputTokens: scenario == "budget" ? 1 : 32000, MaxOutputTokens: 6000));
        using var cancel = new CancellationTokenSource();
        if (scenario == "cancelled")
        {
            cancel.Cancel();
        }

        LlmToolCall[] choices = scenario == "invalid-arguments" ? [new("read", "get_recent_alarms", """{"assetId":"outside-scope"}""")] : [];
        var gateway = new ScriptedLlmGateway([new("{}", choices, "script", new(100, 20))]);
        var reads = 0;
        var error = await Record.ExceptionAsync(() => new InvestigationEngine(gateway, Options.Create(new AiOptions()))
            .ExecuteAsync(evidence, (_, _, _) =>
            {
                reads++;
                throw new InvalidOperationException();
            }, token =>
            {
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }, budget, cancel.Token));
        Assert.NotNull(error);
        if (scenario == "cancelled")
        {
            Assert.IsAssignableFrom<OperationCanceledException>(error);
        }
        else
        {
            Assert.Equal(scenario == "budget" ? LlmFailure.BudgetExceeded
            : scenario == "invalid-arguments" ? LlmFailure.InvalidRequest : LlmFailure.InvalidResponse, Assert.IsType<LlmException>(error).Failure);
        }

        Assert.Equal(0, reads);
        Assert.Contains("NoValidAdvice", AgentEvidenceAcceptance.Check(choices, [], evidence, null));
    }
}

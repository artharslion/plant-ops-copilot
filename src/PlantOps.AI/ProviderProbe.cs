using System.Diagnostics;
using System.Text.Json;
using PlantOps.Application.AI;

namespace PlantOps.AI;

public sealed record ProviderProbeCheck(string Capability, string Status, string? Failure,
    int? ReportedInputTokens = null, int? ReportedOutputTokens = null);

public sealed record ProviderProbeBudget(string Case, int Rounds, int Attempts, int ReservedInputTokens, int ReservedOutputTokens);

public sealed record ProviderProbeReport(bool Simulated, bool ChecksPassed, bool RealProviderVerified,
    string Provider, string Model, string PromptVersion, ProviderProbeCheck[] Checks,
    int Rounds, int Attempts, int ToolCalls, int ReservedInputTokens, int ReservedOutputTokens,
    int? ReportedInputTokens, int? ReportedOutputTokens, decimal? EstimatedCost, long ElapsedMilliseconds,
    ProviderProbeBudget[] CaseBudgets);

public static class ProviderProbe
{
    public const string ProbeObservation = "Synthetic probe vibration is 12.6 mm/s.";
    private static readonly LlmTool Tool = new("get_asset_details", "Read a synthetic asset for a protocol probe; no business data.",
        JsonDocument.Parse("""{"type":"object","additionalProperties":false,"required":["assetId"],"properties":{"assetId":{"type":"string","enum":["demo-p101"]}}}""").RootElement.Clone());

    public static FakeLlmGateway CreateFake() => new([
        new("Hello.", [], "fake", null),
        new(null, [new("probe_call_1", "get_asset_details", """{"assetId":"demo-p101"}""")], "fake", null),
        new("E1", [], "fake", null),
        new(InvestigationContract.Serialize(new("1", "Synthetic protocol probe; no equipment diagnosis.",
            [new(ProbeObservation, ["E1"])], [], [], ["No business or field data was used."], [], ["E1"])), [], "fake", null)
    ]);

    public static async Task<ProviderProbeReport> RunAsync(ILlmGateway gateway, AiOptions options, bool simulated, CancellationToken ct = default)
    {
        var timer = Stopwatch.StartNew();
        using var basicBudget = new LlmBudget(options.Limits);
        using var toolBudget = new LlmBudget(options.Limits);
        using var schemaBudget = new LlmBudget(options.Limits);
        // Three independent compatibility cases, not one business investigation.
        // Each case retains the investigation limits; the complete suite has one 60s deadline.
        using var suiteDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.RunTimeoutSeconds));
        using var suiteCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, suiteDeadline.Token);
        var budget = basicBudget;
        var checks = new List<ProviderProbeCheck>();
        var usageComplete = true;
        var input = 0;
        var output = 0;
        async Task<LlmCompletion?> Check(string capability, LlmRequest request, Action<LlmCompletion> validate)
        {
            try
            {
                var result = await gateway.CompleteAsync(request, budget, suiteCancellation.Token);
                if (result.Usage is null)
                {
                    usageComplete = false;
                }
                else
                {
                    input += result.Usage.InputTokens;
                    output += result.Usage.OutputTokens;
                }
                validate(result);
                checks.Add(new(capability, "Passed", null, result.Usage?.InputTokens, result.Usage?.OutputTokens));
                return result;
            }
            catch (LlmException ex)
            {
                usageComplete = false;
                checks.Add(new(capability, "Failed", ex.Failure.ToString(), ex.Usage?.InputTokens, ex.Usage?.OutputTokens));
                return null;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
            {
                usageComplete = false;
                checks.Add(new(capability, "Failed", LlmFailure.InvalidResponse.ToString()));
                return null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && suiteDeadline.IsCancellationRequested)
            {
                usageComplete = false;
                checks.Add(new(capability, "Failed", LlmFailure.Timeout.ToString()));
                return null;
            }
        }
        await Check("BasicCompletion", new([new(LlmRole.User, "Say hello in one sentence. This is a synthetic protocol probe.")], MaxOutputTokens: 128), result =>
        {
            if (string.IsNullOrWhiteSpace(result.Content) || result.ToolCalls.Count != 0)
            {
                throw new LlmException(LlmFailure.InvalidResponse);
            }
        });
        budget = toolBudget;
        LlmMessage[] toolMessages = [new(LlmRole.System, "This is a synthetic protocol probe. Call only the specified read-only tool."),
            new(LlmRole.User, "Call get_asset_details for assetId demo-p101.")];
        var toolResult = await Check("ReadOnlyToolCall", new(toolMessages, [Tool], RequiredTool: Tool.Name, MaxOutputTokens: 256), result =>
        {
            if (result.ToolCalls.Count != 1 || result.ToolCalls[0].Name != Tool.Name)
            {
                throw new LlmException(LlmFailure.InvalidResponse);
            }
            using var args = JsonDocument.Parse(result.ToolCalls[0].Arguments);
            if (args.RootElement.ValueKind != JsonValueKind.Object || args.RootElement.EnumerateObject().Count() != 1
                || !args.RootElement.TryGetProperty("assetId", out var assetId) || assetId.GetString() != "demo-p101")
            {
                throw new LlmException(LlmFailure.InvalidResponse);
            }
        });
        if (toolResult is not null)
        {
            await Check("ToolResultContinuation", new([
                .. toolMessages, new(LlmRole.Assistant, toolResult.Content, toolResult.ToolCalls),
                new(LlmRole.Tool, """{"evidenceId":"E1","assetId":"demo-p101","value":12.6,"unit":"mm/s","source":"SyntheticProbe"}""", ToolCallId: toolResult.ToolCalls[0].Id),
                new(LlmRole.User, "The tool returned synthetic evidence E1. Do not call any more tools. Reply with exactly E1.")
            ], [Tool], MaxOutputTokens: 128, DisableToolCalls: true), result =>
            {
                if (result.Content?.Trim() != "E1" || result.ToolCalls.Count != 0)
                {
                    throw new LlmException(LlmFailure.InvalidResponse);
                }
            });
        }
        else
        {
            checks.Add(new("ToolResultContinuation", "NotRun", "ToolCallFailed"));
        }
        budget = schemaBudget;
        await Check("InvestigationStrictSchema", new([
            new(LlmRole.System, InvestigationContract.SystemPrompt),
            new(LlmRole.User, "Synthetic protocol probe. The sole authorized evidence is E1: vibration=12.6 mm/s, source=SyntheticProbe. "
                + "Return schemaVersion 1, a short summary, one observation with text exactly '" + ProbeObservation
                + "' and evidenceIds [E1]. hypotheses and nextChecks must be empty. "
                + "missingInformation must state no field data was used. dataWarnings must be empty. Top-level evidenceIds must be [E1].")
        ], OutputSchema: InvestigationContract.OutputSchema, MaxOutputTokens: Math.Max(1, Math.Min(1200, budget.RemainingOutputTokens))), result =>
        {
            if (result.Content is null || result.ToolCalls.Count != 0)
            {
                throw new LlmException(LlmFailure.InvalidResponse);
            }
            var parsed = InvestigationContract.ParseAndValidateReferences(result.Content, new HashSet<string>(["E1"], StringComparer.Ordinal));
            if (parsed.Observations.Length != 1 || parsed.Observations[0].Text != ProbeObservation
                || !parsed.Observations[0].EvidenceIds.SequenceEqual(["E1"]) || parsed.Hypotheses.Length != 0
                || parsed.NextChecks.Length != 0 || parsed.MissingInformation.Length == 0)
            {
                throw new LlmException(LlmFailure.InvalidResponse);
            }
        });
        var passed = checks.All(x => x.Status == "Passed");
        LlmBudget[] budgets = [basicBudget, toolBudget, schemaBudget];
        var rounds = budgets.Sum(x => x.ModelRounds);
        var attempts = budgets.Sum(x => x.Attempts);
        // Retries may have incurred unknown usage even when the final attempt succeeds.
        usageComplete &= attempts == rounds;
        return new(simulated, passed, passed && !simulated, simulated ? "Fake" : options.Provider, simulated ? "fake" : options.Model,
            InvestigationContract.PromptVersion, checks.ToArray(), rounds, attempts, budgets.Sum(x => x.ToolCalls),
            budgets.Sum(x => x.ReservedInputTokens), budgets.Sum(x => x.ReservedOutputTokens), usageComplete ? input : null, usageComplete ? output : null,
            null, timer.ElapsedMilliseconds,
            [new("BasicCompletion", basicBudget.ModelRounds, basicBudget.Attempts, basicBudget.ReservedInputTokens, basicBudget.ReservedOutputTokens),
             new("ToolRoundTrip", toolBudget.ModelRounds, toolBudget.Attempts, toolBudget.ReservedInputTokens, toolBudget.ReservedOutputTokens),
             new("InvestigationStrictSchema", schemaBudget.ModelRounds, schemaBudget.Attempts, schemaBudget.ReservedInputTokens, schemaBudget.ReservedOutputTokens)]);
    }
}

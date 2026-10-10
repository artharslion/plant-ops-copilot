using PlantOps.Application.AI;

namespace PlantOps.AI;

// Deterministic test double. It never reads a database or invents business evidence.
public sealed class FakeLlmGateway : ILlmGateway
{
    private readonly Queue<LlmCompletion>? script;

    public FakeLlmGateway()
    {
    }

    public FakeLlmGateway(IEnumerable<LlmCompletion> script) => this.script = new(script);

    public Task<LlmCompletion> CompleteAsync(LlmRequest request, LlmBudget budget, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = ChatCompletionsProtocol.Serialize(request, "fake");
        budget.BeginRound();
        budget.ReserveAttempt(bytes.Length + 256, request.MaxOutputTokens);
        LlmCompletion result;
        if (script is not null)
        {
            if (!script.TryDequeue(out var next))
            {
                throw new LlmException(LlmFailure.InvalidResponse);
            }
            result = next;
        }
        else
        {
            var business = request.Messages.FirstOrDefault(x => x.Role == LlmRole.User && x.Content?.Contains(InvestigationEngine.PromptVersion, StringComparison.Ordinal) == true);
            string? businessContent = null;
            if (business != null)
            {
                using var input = System.Text.Json.JsonDocument.Parse(business.Content!);
                var item = input.RootElement.GetProperty("evidence").EnumerateArray().First();
                var id = item.GetProperty("id").GetString()!;
                var fact = item.GetProperty("facts")[0].GetString()!;
                businessContent = InvestigationContract.Serialize(new("1", InvestigationEngine.SafeSummary,
                    [new(fact, [id])], [], [new("请维护人员结合已授权规程核实现场情况。", [id])], ["模拟结果，尚无真实模型诊断或现场检查。"], [], [id]));
            }
            var content = businessContent ?? (request.OutputSchema is null ? "Fake provider: no live model was called."
                : InvestigationContract.Serialize(new("1", "模拟响应：尚未连接真实模型。", [], [], [], ["尚无调查证据。"], [], [])));
            result = new(content, [], "fake", null);
        }
        budget.ReconcileUsage(result.Usage, bytes.Length + 256, request.MaxOutputTokens);
        budget.RecordToolCalls(result.ToolCalls.Count);
        return Task.FromResult(result);
    }
}

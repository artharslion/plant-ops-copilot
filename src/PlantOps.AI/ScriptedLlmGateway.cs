using PlantOps.Application.AI;

namespace PlantOps.AI;

// Explicit responses for deterministic protocol/orchestration tests. No diagnosis,
// tool selection or fallback to demo behavior when the script is exhausted.
public sealed class ScriptedLlmGateway(IEnumerable<LlmCompletion> script) : ILlmGateway
{
    private readonly Queue<LlmCompletion> responses = new(script);

    public Task<LlmCompletion> CompleteAsync(LlmRequest request, LlmBudget budget,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = ChatCompletionsProtocol.Serialize(request, "fake");
        budget.BeginRound();
        budget.ReserveAttempt(bytes.Length + 256, request.MaxOutputTokens);
        if (!responses.TryDequeue(out var response))
        {
            throw new LlmException(LlmFailure.InvalidResponse);
        }
        budget.ReconcileUsage(response.Usage, bytes.Length + 256, request.MaxOutputTokens);
        budget.RecordToolCalls(response.ToolCalls.Count);
        return Task.FromResult(response);
    }
}

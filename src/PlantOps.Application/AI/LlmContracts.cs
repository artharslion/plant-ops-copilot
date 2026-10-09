using System.Text.Json;

namespace PlantOps.Application.AI;

public enum LlmRole
{
    System, User, Assistant, Tool
}

public sealed record LlmToolCall(string Id, string Name, string Arguments);

public sealed record LlmMessage(LlmRole Role, string? Content, IReadOnlyList<LlmToolCall>? ToolCalls = null, string? ToolCallId = null);

public sealed record LlmTool(string Name, string Description, JsonElement Parameters);

public sealed record LlmOutputSchema(string Name, JsonElement Schema);

public sealed record LlmRequest(IReadOnlyList<LlmMessage> Messages, IReadOnlyList<LlmTool>? Tools = null,
    LlmOutputSchema? OutputSchema = null, string? RequiredTool = null, int MaxOutputTokens = 500, bool DisableToolCalls = false);

public sealed record LlmUsage(int InputTokens, int OutputTokens);

public sealed record LlmCompletion(string? Content, IReadOnlyList<LlmToolCall> ToolCalls, string Model, LlmUsage? Usage);

public interface ILlmGateway
{
    Task<LlmCompletion> CompleteAsync(LlmRequest request, LlmBudget budget, CancellationToken cancellationToken = default);
}

public enum LlmFailure
{
    NotConfigured, InvalidConfiguration, InvalidRequest, BudgetExceeded, Timeout,
    Unavailable, RateLimited, Unauthorized, UnsupportedRequest, InvalidResponse, Refused, Truncated
}

// Never include provider response bodies, messages, URLs with credentials or inner exceptions.
public sealed class LlmException(LlmFailure failure, LlmUsage? usage = null) : Exception($"AI request failed: {failure}.")
{
    public LlmFailure Failure { get; } = failure;

    public LlmUsage? Usage { get; } = usage;
}

using System.Text.Json;
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
                var observations = input.RootElement.TryGetProperty("requiredObservations", out var required)
                    ? required.Deserialize<InvestigationObservation[]>(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!
                    : input.RootElement.GetProperty("evidence").EnumerateArray().SelectMany(e =>
                {
                    var kind = e.GetProperty("kind").GetString();
                    var facts = e.GetProperty("facts").EnumerateArray().Select(x => x.GetString()!).ToArray();
                    var selected = kind == "Alarm" ? facts.Take(2)
                        : kind is "EventTrend" or "RecoveryTrend" or "Trend" && !facts.Any(x => x.Contains("事件所在分钟尚未结束", StringComparison.Ordinal))
                            ? facts.Where(x => x.Contains("最小值为", StringComparison.Ordinal)).Take(1) : [];
                    return selected.Select(x => new InvestigationObservation(x, [e.GetProperty("id").GetString()!]));
                }).ToArray();
                var serverFacts = input.RootElement.TryGetProperty("factRendering", out var rendering) && rendering.GetString() == "server";
                var english = input.RootElement.TryGetProperty("language", out var language) && language.GetString() == "en";
                var rawChange = input.RootElement.GetProperty("evidence").EnumerateArray().FirstOrDefault(e =>
                    e.GetProperty("kind").GetString() is "EventDerivedTrend" or "CurrentDerivedTrend"
                    && (e.GetProperty("facts").EnumerateArray().Any(f => f.GetString()!.Contains("有序原始观测：", StringComparison.Ordinal)
                        || f.GetString()!.Contains("非下降观测段：", StringComparison.Ordinal))
                        || observations.Any(o => o.EvidenceIds.Contains(e.GetProperty("id").GetString()!, StringComparer.Ordinal)
                            && (o.Text.Contains("有序原始观测：", StringComparison.Ordinal) || o.Text.Contains("非下降观测段：", StringComparison.Ordinal)))));
                var alarm = input.RootElement.GetProperty("evidence").EnumerateArray().FirstOrDefault(e => e.GetProperty("kind").GetString() == "Alarm");
                InvestigationObservation[] analysis = rawChange.ValueKind == JsonValueKind.Undefined || alarm.ValueKind == JsonValueKind.Undefined ? []
                    : [new(english ? "The ordered change provides trend context for the selected historical alarm; current observations require separate assessment and the cause remains unconfirmed."
                        : "有序观测变化为所选历史告警提供趋势依据；当前观测需要单独核对，异常原因仍待核查。",
                        [rawChange.GetProperty("id").GetString()!, alarm.GetProperty("id").GetString()!])];
                observations = serverFacts ? [] : observations.Length == 0 ? [new(fact, [id])] : observations;
                var derivedMode = request.OutputSchema?.Name == "investigation_v2";
                businessContent = InvestigationContract.Serialize(new(derivedMode ? "2" : "1", english ? InvestigationEngine.EnglishSafeSummary : InvestigationEngine.SafeSummary,
                    observations, [], [new(english ? "Review the historical alarm snapshot and event observations." : "核对告警历史快照与事件期观测。", [id],
                        english ? "Record the asset, trigger and recovery times, event evidence and unavailable records." : "记录对象、触发与恢复时间、事件窗口证据及无法取得的记录。",
                        english ? "Complete once available evidence and missing items are documented and reviewed by maintenance personnel." : "已逐项记录可查证据及缺项，并经维护人员复核。",
                        english ? "Refer insufficient evidence or recurring anomalies to a maintenance engineer." : "证据不足或异常复发时交维护工程师复核。")],
                    [english ? "Simulated result; no real model diagnosis or on-site inspection." : "模拟结果，尚无真实模型诊断或现场检查。"], [],
                    observations.SelectMany(x => x.EvidenceIds).Concat(analysis.SelectMany(x => x.EvidenceIds)).Append(id).Distinct().ToArray(), derivedMode ? analysis : null));
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

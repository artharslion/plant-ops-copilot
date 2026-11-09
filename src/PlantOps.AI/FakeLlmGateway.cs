using System.Text.Json;
using System.Text.Json.Nodes;
using PlantOps.Application.AI;

namespace PlantOps.AI;

// Local demo simulation with fixed tool choices and generated canned advice.
// Never use its output to accept real-model reasoning quality. Use ScriptedLlmGateway
// for deterministic orchestration, budget and failure tests.
public sealed class FakeLlmGateway : ILlmGateway
{
    public Task<LlmCompletion> CompleteAsync(LlmRequest request, LlmBudget budget, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.OutputSchema?.Name == "investigation_v3")
        {
            using var context = JsonDocument.Parse(request.Messages.First(x => x.Role == LlmRole.User).Content!);
            var input = context.RootElement;
            if (!request.DisableToolCalls && request.Tools?.Count > 0 && input.GetProperty("toolOutcomes").GetArrayLength() == 0
                && input.GetProperty("evidence").EnumerateArray().Any(x => x.GetProperty("kind").GetString() == "PointStatus")
                && !input.GetProperty("evidence").EnumerateArray().Any(x => x.GetProperty("kind").GetString() == "EventDerivedTrend"))
            {
                return new ScriptedLlmGateway([new(null,
                    [new("measurements", "get_recent_measurements", "{}"), new("alarms", "get_recent_alarms", "{}"),
                     new("maintenance", "get_maintenance_history", "{}"),
                     new("documents", "search_documents", "{\"query\":\"排查 vibration 温度\",\"limit\":6}")],
                    "fake", new(100, 20))]).CompleteAsync(request, budget, cancellationToken);
            }
            return new ScriptedLlmGateway([new(InvestigationAdviceContract.Serialize(SimulateAdvice(input)), [], "fake", null)])
                .CompleteAsync(request, budget, cancellationToken);
        }

        // The default simulation deliberately exercises the read-only tool pipeline.
        // It is not evidence of real-model tool selection or reasoning quality.
        if (budget.ToolTrace.Count <= 1 && !request.DisableToolCalls && request.Messages.All(x => x.Role != LlmRole.Tool))
        {
            var initial = request.Messages.FirstOrDefault(x => x.Role == LlmRole.User)?.Content;
            if (initial != null && initial.Contains("PointStatus", StringComparison.Ordinal)
                && !initial.Contains("EventDerivedTrend", StringComparison.Ordinal))
            {
                return new ScriptedLlmGateway([new(null,
                    [new("measurements", "get_recent_measurements", "{}"),
                     new("alarms", "get_recent_alarms", "{}"),
                     new("maintenance", "get_maintenance_history", "{}"),
                     new("documents", "search_documents", "{\"query\":\"排查 vibration 温度\",\"limit\":6}")],
                    "fake", new(100, 20))]).CompleteAsync(request, budget, cancellationToken);
            }
        }
        var business = request.Messages.FirstOrDefault(x => x.Role == LlmRole.User && x.Content?.Contains(LegacyInvestigationEngine.PromptVersion, StringComparison.Ordinal) == true);
        string? businessContent = null;
        if (business != null)
        {
            var combined = JsonNode.Parse(business.Content!)!.AsObject();
            foreach (var message in request.Messages.Where(x => x.Role == LlmRole.Tool))
            {
                var tool = JsonNode.Parse(message.Content!)!;
                foreach (var entry in tool["evidence"]!.AsArray())
                {
                    if (!combined["evidence"]!.AsArray().Any(x => x!["id"]!.GetValue<string>() == entry!["id"]!.GetValue<string>()))
                    {
                        combined["evidence"]!.AsArray().Add(entry!.DeepClone());
                    }
                }
                if (tool["requiredObservations"] is { } requiredFacts)
                {
                    combined["requiredObservations"] = requiredFacts.DeepClone();
                }
            }
            using var input = JsonDocument.Parse(combined.ToJsonString());
            var item = input.RootElement.GetProperty("evidence").EnumerateArray().First();
            var id = item.GetProperty("id").GetString()!;
            var fact = item.GetProperty("facts").GetArrayLength() > 0 ? item.GetProperty("facts")[0].GetString()!
                : input.RootElement.GetProperty("requiredObservations")[0].GetProperty("text").GetString()!;
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
            var stateClaims = input.RootElement.GetProperty("evidence").EnumerateArray()
                .Where(e => e.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object)
                .Select(e => new InvestigationStateClaim(e.GetProperty("id").GetString()!,
                    e.GetProperty("state").GetProperty("availability").GetString()!,
                    e.GetProperty("state").GetProperty("currentTrust").GetString(),
                    e.GetProperty("state").GetProperty("currentAnomaly").GetString())).ToArray();
            businessContent = InvestigationContract.Serialize(new(derivedMode ? "2" : "1", english ? LegacyInvestigationEngine.EnglishSafeSummary : LegacyInvestigationEngine.SafeSummary,
                observations, [], [new(english ? "Review the historical alarm snapshot and event observations." : "核对告警历史快照与事件期观测。", [id],
                    english ? "Record the asset, trigger and recovery times, event evidence and unavailable records." : "记录对象、触发与恢复时间、事件窗口证据及无法取得的记录。",
                    english ? "Complete once available evidence and missing items are documented and reviewed by maintenance personnel." : "已逐项记录可查证据及缺项，并经维护人员复核。",
                    english ? "Refer insufficient evidence or recurring anomalies to a maintenance engineer." : "证据不足或异常复发时交维护工程师复核。")],
                [english ? "Simulated result; no real model diagnosis or on-site inspection." : "模拟结果，尚无真实模型诊断或现场检查。"], [],
                null!, derivedMode ? analysis : null,
                stateClaims.Length == 0 ? null : stateClaims));
        }
        var content = businessContent ?? (request.OutputSchema is null ? "Fake provider: no live model was called."
            : InvestigationContract.Serialize(new("1", "模拟响应：尚未连接真实模型。", [], [], [], ["尚无调查证据。"], [], [])));
        return new ScriptedLlmGateway([new(content, [], "fake", null)])
            .CompleteAsync(request, budget, cancellationToken);
    }

    internal static InvestigationAdvice SimulateAdvice(JsonElement input)
    {
        var english = input.GetProperty("language").GetString() == "en";
        string L(string zh, string en) => english ? en : zh;
        var evidence = input.GetProperty("evidence").EnumerateArray().ToArray();
        var subjects = input.GetProperty("subjects").EnumerateArray().ToArray();
        var findings = new List<InvestigationFinding>();
        var recommendations = new List<InvestigationRecommendation>();
        var gaps = new List<InvestigationGap>();
        foreach (var item in evidence.Where(x => x.GetProperty("kind").GetString() == "PointStatus"))
        {
            var point = item.GetProperty("point");
            var id = item.GetProperty("id").GetString()!;
            var subject = item.GetProperty("subjectRef").GetString()!;
            var trusted = point.GetProperty("currentTrust").GetString() == "Trusted";
            var anomaly = point.GetProperty("currentAnomaly").GetString();
            var status = trusted ? anomaly == "Present" ? "AnomalySupported" : anomaly == "NotFound" ? "NoAnomalyFound" : "Inconclusive" : "Inconclusive";
            findings.Add(new("F" + (findings.Count + 1), [subject], "Current", trusted ? "EquipmentCondition" : "MeasurementChain", status,
                !trusted ? L("当前观测不可验证，需要核对测量链。", "Current observations are unverifiable; review the measurement chain.")
                    : anomaly == "Present" ? L("当前观测支持已检查测点存在阈值异常，原因待核查。", "Current observations support a threshold anomaly at the inspected point; cause needs verification.")
                    : anomaly == "NotFound" ? L("已检查范围内未发现当前阈值异常，历史事件仍需单独核查。", "No current threshold anomaly was found in the inspected scope; review the historical event separately.")
                    : L("缺少适用的当前阈值判断依据。", "An applicable current threshold assessment is unavailable."), [id]));
            var recommendationId = "R" + (recommendations.Count + 1);
            recommendations.Add(new(recommendationId, trusted ? "Inspect" : "VerifyMeasurement", [subject], [id],
                trusted ? L("核对事件工况并比对独立观测记录。", "Review event operating conditions and independent observation records.")
                    : L("核对测点质量、来源健康及映射配置。", "Review point quality, source health and mapping configuration."),
                L("记录核查依据、差异及仍无法核实的范围。", "Record evidence, discrepancies and remaining unverifiable scope."),
                L("核查结果与剩余缺项已记录并经维护工程师复核。", "Complete after findings and remaining gaps are documented and reviewed by a maintenance engineer."),
                new(L("异常持续或证据仍不足时提交复核。", "Escalate persistent anomalies or unresolved evidence gaps."), "MaintenanceEngineer")));
            if (!trusted)
            {
                gaps.Add(new("G" + (gaps.Count + 1), "InsufficientDetail",
                    L("仍需核实当前测量链及观测质量。", "Current measurement provenance and quality require verification."),
                    L("限制对当前设备状态的判断，不妨碍安排测量链核查。", "This limits current equipment assessment but allows measurement verification work."), [id], [recommendationId]));
            }
        }
        foreach (var item in evidence.Where(x => x.GetProperty("kind").GetString() is "EventDerivedTrend" or "CurrentDerivedTrend"))
        {
            var trend = item.GetProperty("trend");
            if (trend.ValueKind != JsonValueKind.Object || !trend.TryGetProperty("change", out var change)
                || change.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            findings.Add(new("F" + (findings.Count + 1), [item.GetProperty("subjectRef").GetString()!],
                item.GetProperty("kind").GetString() == "CurrentDerivedTrend" ? "Current" : "AlarmEvent",
                "MeasurementChain", "AnomalySupported",
                L("已提供有序观测变化；仅解释该窗口，不证明物理连续过程或原因。", "Ordered observation changes are supplied for this window; they establish neither physical continuity nor cause."),
                [item.GetProperty("id").GetString()!]));
        }
        if (recommendations.Count == 0)
        {
            var first = evidence.First();
            recommendations.Add(new("R1", "Inspect", [subjects[0].GetProperty("ref").GetString()!], [first.GetProperty("id").GetString()!],
                L("核对告警事件及现场检查记录。", "Review the alarm event and on-site inspection records."),
                L("记录已有依据和缺项。", "Record available evidence and gaps."),
                L("已有依据与缺项经维护工程师复核后完成。", "Complete after evidence and gaps are reviewed by a maintenance engineer."),
                new(L("证据不足时提交维护工程师复核。", "Refer unresolved uncertainty to a maintenance engineer."), "MaintenanceEngineer")));
        }
        var document = evidence.FirstOrDefault(x => x.GetProperty("kind").GetString() == "Document");
        if (document.ValueKind != JsonValueKind.Undefined)
        {
            recommendations.Add(new("R" + (recommendations.Count + 1), "ReviewProcedure", [subjects[0].GetProperty("ref").GetString()!],
                [document.GetProperty("id").GetString()!], L("核对授权规程的适用范围与现场检查记录。", "Review the authorized procedure scope against on-site inspection records."),
                L("记录版本、适用范围及差异。", "Record procedure version, applicability and discrepancies."),
                L("规程适用性及差异已由维护工程师复核。", "Complete after applicability and discrepancies are reviewed by a maintenance engineer."),
                new(L("资料不适用时提交维护工程师复核。", "Refer inapplicable guidance to a maintenance engineer."), "MaintenanceEngineer")));
        }

        return new("3", gaps.Count > 0 ? "NeedsMoreEvidence" : "Ready",
            L("模拟建议仅用于演示流程，现场判断仍需工程师复核。", "Simulated advice demonstrates the workflow and requires engineer review."),
            gaps.Select(x => x.Id).ToArray(),
            L("已整理告警核查建议与诊断工单草稿。", "Alarm review advice and a diagnostic work-order draft are available."),
            findings.Take(8).ToArray(), [], recommendations.Take(6).ToArray(), gaps.ToArray(),
            new("DraftReady", L("已有明确的核查任务，可先形成诊断工单。", "Defined verification tasks allow a diagnostic work-order draft."), [],
                new(L("告警事件与测量链核查", "Alarm event and measurement-chain review"), "Medium",
                    L("按现有证据安排核查，紧急程度由工程师确认。", "Plan verification based on available evidence; an engineer reviews urgency."),
                    L("记录异常依据、核查结果和剩余缺项。", "Document anomaly evidence, verification findings and remaining gaps."),
                    recommendations.Take(6).Select(x => x.Id).ToArray())));
    }

}

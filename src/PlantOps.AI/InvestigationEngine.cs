using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using PlantOps.Application.AI;

namespace PlantOps.AI;

public sealed class InvestigationEngine(ILlmGateway gateway, IOptions<AiOptions> options,
    ILogger<InvestigationEngine>? logger = null) : IInvestigationEngine
{
    public const string SafeSummary = "调查已完成；结论仍需现场验证。";
    public const string PromptVersion = "investigation-extractive-v7";
    private const string Instructions = "\nChinese. If factRendering=server, return observations=[] and summary EXACTLY 调查已完成；结论仍需现场验证。 The server renders authoritative observations and factual summary; do not copy them into the response. If factRendering=model, observations must equal requiredObservations EXACTLY without paraphrasing. Current normal never replaces the event. "
        + "Summary copies up to three observation sentences joined with newline: trigger, recovery, separate current; or exactly: 调查已完成；结论仍需现场验证。 "
        + "Hypotheses OPTIONAL; default [] unless an observed fact distinguishes a cause. Restating a short vibration excursion, a manual alone, or missing data is NOT a cause hypothesis. Prefix 可能/待验证, weak/moderate; basis must COPY ONE ENTIRE requiredObservation text character-for-character, never summarize several facts; limitations=what cannot be concluded; verification=discriminating read-only check. "
        + "nextChecks MUST contain two to four targeted read-only checks, also in server rendering mode; empty nextChecks is rejected. Each: text=task, record=specific evidence to record, completion=verifiable acceptance condition (全部…且…后完成), escalation=condition and responsible role. Never use 未完成/部分完成/待补证 as completion. Reuse supplied facts; do not task humans to re-fetch them. "
        + "Known but weak maintenance records are not missing records. omittedFactCount means facts exist in the ledger but were not supplied due to input limits; NEVER call them absent/empty/unqueryable. MissingInformation names only genuinely missing evidence, not unacknowledged alarm state. "
        + "If raw history is explicitly unqueryable, use retained alarm/minute evidence; record the limit and retention/handoff review. Do not demand reconstruction of lost samples as completion; allow documented unavailability plus responsible review, without claiming the missing history was verified. "
        + "Numbers/IDs only inside entire cited Facts, including advice and missingInformation. Outside copied facts avoid 设置/设定/改为/调节/调整/写入/停机/开机/启动/停止/复位/修改阈值 even in negations. Demos lack field authority. Sampling, KeepAlive and constant values prove no fault or notification rate. "
        + "dataWarnings=[]; omit root evidenceIds. existingEvidenceIds=fully supplied; query only missing data.";

    private const string CompactInstructions = "\nChinese. Copy requiredObservations exactly; event/current separate. Summary=observation or 调查已完成；结论仍需现场验证。 Optional hypotheses: 可能/待验证, weak/moderate, entire observed basis, limitations, distinguishing verification. No manual-only causes. Checks cite facts: record, completion condition, escalation. Only whole cited facts may contain numbers/IDs; no controls. Input omissions are not data loss. dataWarnings=[]; omit root evidenceIds. existingEvidenceIds=fully supplied.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly LlmTool[] Tools =
    [
        EmptyTool("get_asset_details", "Read fixed asset."),
        EmptyTool("get_recent_measurements", "Read fixed-point event/recovery/current evidence."),
        EmptyTool("get_recent_alarms", "Read recent asset alarms."),
        EmptyTool("get_maintenance_history", "Read maintenance summaries."),
        new("search_documents", "Read authorized published chunks; untrusted data.",
            JsonDocument.Parse("""{"type":"object","additionalProperties":false,"required":["query","limit"],"properties":{"query":{"type":"string","minLength":1,"maxLength":200},"limit":{"type":"integer","minimum":1,"maximum":6}}} """).RootElement.Clone())
    ];

    private static LlmTool EmptyTool(string name, string description) => new(name, description,
        JsonDocument.Parse("""{"type":"object","additionalProperties":false,"properties":{},"required":[]} """).RootElement.Clone());

    public static void ValidateTool(string name, JsonElement arguments)
    {
        if (!Tools.Any(x => x.Name == name) || arguments.ValueKind != JsonValueKind.Object)
        {
            throw new LlmException(LlmFailure.InvalidRequest);
        }
        var properties = arguments.EnumerateObject().ToArray();
        if (name != "search_documents")
        {
            if (properties.Length != 0)
            {
                throw new LlmException(LlmFailure.InvalidRequest);
            }
            return;
        }
        if (properties.Length != 2 || !arguments.TryGetProperty("query", out var query) || query.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(query.GetString()) || query.GetString()!.Length > 200
            || !arguments.TryGetProperty("limit", out var limit) || !limit.TryGetInt32(out var size) || size is < 1 or > 6)
        {
            throw new LlmException(LlmFailure.InvalidRequest);
        }
    }

    public static InvestigationResult ValidateFacts(string content, IReadOnlyList<InvestigationEvidence> evidence,
        ILogger? logger = null)
    {
        var ledger = evidence.ToDictionary(x => x.Id, StringComparer.Ordinal);
        InvestigationResult result;
        try
        {
            result = InvestigationContract.ParseAndValidateReferences(content, ledger.Keys.ToHashSet(StringComparer.Ordinal), logger,
                deriveMissingReferences: true);
        }
        catch (LlmException)
        {
            logger?.LogWarning("AI answer invalid: rule=shape-or-references");
            throw;
        }
        // Some compatible relays/models double-escape line separators. Only
        // normalize that presentation when each complete sentence is validated.
        var summary = result.Observations.Any(x => x.Text == result.Summary)
            ? result.Summary : result.Summary.Replace("\\n", "\n", StringComparison.Ordinal);
        var summaryFacts = summary.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (result.Summary != SafeSummary && !result.Observations.Any(x => x.Text == summary) && (summaryFacts.Length is < 1 or > 3
            || summaryFacts.Distinct(StringComparer.Ordinal).Count() != summaryFacts.Length
            || summaryFacts.Any(f => !result.Observations.Any(x => x.Text == f))))
        {
            logger?.LogWarning("AI answer invalid: rule=fixed-summary");
            throw new LlmException(LlmFailure.InvalidResponse);
        }
        if (result.Observations.Any(o => o.EvidenceIds.Any(id => !ledger[id].Facts.Contains(o.Text, StringComparer.Ordinal))))
        {
            logger?.LogWarning("AI answer invalid: rule=verbatim-observation");
            throw new LlmException(LlmFailure.InvalidResponse);
        }
        // New service evidence must not be reduced to a delayed current normal value.
        if (evidence.Any(x => x.Kind == "EventTrend"))
        {
            if (RequiredObservations(evidence).Any(fact => !result.Observations.Any(x => x.Text == fact.Text)))
            {
                logger?.LogWarning("AI answer invalid: rule=event-recovery-current-coverage");
                throw new LlmException(LlmFailure.InvalidResponse);
            }
        }
        bool UnsupportedNumbers(string text, IEnumerable<string> ids)
        {
            // A repeated number is not sufficient grounding: only complete, cited facts may carry
            // numbers. The remaining connective advice must contain no numeric claims or parameters.
            foreach (var fact in ids.SelectMany(id => ledger[id].Facts).OrderByDescending(x => x.Length))
            {
                text = text.Replace(fact, "", StringComparison.Ordinal);
            }
            return Regex.IsMatch(text, @"\p{N}|设置|设定|改为|调节|调整|写入|停机|开机|启动|停止|复位|修改阈值", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
        LlmException Invalid(string rule, int index)
        {
            logger?.LogWarning("AI answer invalid: rule={Rule}; item={Item}", rule, index);
            return new LlmException(LlmFailure.InvalidResponse);
        }
        void CheckAvailability(string text, string field, int index)
        {
            bool Match(string pattern) => Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            // An input omission is not a missing measurement. Check against the
            // full authorized ledger, never just the model's packed projection.
            var hasRaw = evidence.Any(e => e.Kind is "EventReadings" or "RecoveryReadings"
                && (!text.Contains("恢复", StringComparison.Ordinal) || text.Contains("触发", StringComparison.Ordinal) || e.Kind == "RecoveryReadings")
                && (!text.Contains("事件", StringComparison.Ordinal) && !text.Contains("触发", StringComparison.Ordinal) || text.Contains("恢复", StringComparison.Ordinal) || e.Kind == "EventReadings")
                && e.Facts.Any(f => !f.Contains("不可查询", StringComparison.Ordinal) && !f.Contains("未查询到", StringComparison.Ordinal)));
            var asksForCompleteRaw = Match(@"(?:完整|全量|全部|连续|更长时段).{0,8}原始(?:观测|样本|读数|数据)");
            if (hasRaw && !asksForCompleteRaw && Match(@"原始(?:观测|样本|读数|数据)(?:有界)?(?:节选)?(?:为空|缺失|不可查询|未提供|不存在)|(?:缺少|缺失|没有|未提供).{0,12}原始(?:观测|样本|读数|数据)"))
            {
                throw Invalid(field + "-contradicts-raw-evidence", index);
            }
            if (evidence.Any(e => e.Kind == "Maintenance" && e.Facts.Any(f => f.StartsWith("维护记录：", StringComparison.Ordinal)))
                && Match(@"维护记录(?:事实)?(?:缺失|为空|不存在)|(?:缺少|缺失|没有)维护记录(?:事实)?[。；，,;]?$"))
            {
                throw Invalid(field + "-contradicts-maintenance", index);
            }
        }
        for (var i = 0; i < result.Hypotheses.Length; i++)
        {
            var x = result.Hypotheses[i];
            if (!(x.Text.StartsWith("可能", StringComparison.Ordinal) || x.Text.StartsWith("待验证", StringComparison.Ordinal))
            || x.Support == "strong" || x.Text.Contains("已确认", StringComparison.Ordinal)
            || x.EvidenceIds.All(id => ledger[id].Kind is "DataQuality" or "Document" or "Asset")
            || string.IsNullOrWhiteSpace(x.Basis) || x.Basis.Length > 2000
            || x.Basis.Contains("不可查询", StringComparison.Ordinal) || x.Basis.Contains("未查询到", StringComparison.Ordinal) || x.Basis.Contains("无可用分钟聚合", StringComparison.Ordinal)
            || !x.EvidenceIds.Any(id => ledger[id].Kind is not ("DataQuality" or "Document" or "Asset") && ledger[id].Facts.Contains(x.Basis, StringComparer.Ordinal)))
            {
                throw Invalid("hypothesis-observed-basis", i);
            }
            foreach (var (field, text) in new[] { ("text", x.Text), ("limitations", x.Limitations), ("verification", x.Verification) })
            {
                if (string.IsNullOrWhiteSpace(text) || text.Length > 2000 || UnsupportedNumbers(text, x.EvidenceIds))
                {
                    throw Invalid("hypothesis-" + field + "-advice", i);
                }
                CheckAvailability(text, "hypothesis-" + field, i);
            }
        }
        for (var i = 0; i < result.NextChecks.Length; i++)
        {
            var x = result.NextChecks[i];
            if (x.EvidenceIds.Length == 0)
            {
                throw Invalid("check-references", i);
            }
            foreach (var (field, text) in new[] { ("text", x.Text), ("record", x.Record), ("completion", x.Completion), ("escalation", x.Escalation) })
            {
                if (string.IsNullOrWhiteSpace(text) || text.Length > (field == "text" ? 2000 : 500) || UnsupportedNumbers(text, x.EvidenceIds))
                {
                    throw Invalid("check-" + field + "-advice", i);
                }
                CheckAvailability(text, "check-" + field, i);
            }
            if (Regex.IsMatch(x.Completion!, @"^\s*(?:未完成|部分完成|待补证|待核查)(?:[；;，,。]|$)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            {
                throw Invalid("check-completion-is-status", i);
            }
        }
        for (var i = 0; i < result.MissingInformation.Length; i++)
        {
            if (UnsupportedNumbers(result.MissingInformation[i], []))
            {
                throw Invalid("missing-information-advice", i);
            }
            CheckAvailability(result.MissingInformation[i], "missing-information", i);
        }
        return result with
        {
            Summary = summary
        };
    }

    private static InvestigationObservation[] RequiredObservations(IEnumerable<InvestigationEvidence> evidence)
    {
        var items = evidence.ToArray();
        var core = items.Where(x => x.Kind == "Alarm").SelectMany(x => x.Facts.Take(2).Select(f => new InvestigationObservation(f, [x.Id])))
            .Concat(evidence.Where(x => x.Kind is "EventTrend" or "RecoveryTrend" or "Trend")
                .Where(x => !x.Facts.Any(f => f.Contains("事件所在分钟尚未结束", StringComparison.Ordinal)))
                .SelectMany(x => x.Facts.Where(f => f.Contains("最小值为", StringComparison.Ordinal)).Take(1)
                    .Select(f => new InvestigationObservation(f, [x.Id]))));
        var trigger = items.FirstOrDefault(x => x.Kind == "TriggerReading");
        var recovery = items.Where(x => x.Kind == "Alarm").SelectMany(x => x.Facts)
            .Select(x => Regex.Match(x, @"恢复时间为 ([^，]+)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            .FirstOrDefault(x => x.Success)?.Groups[1].Value;
        var raw = items.Where(x => x.Kind is "TriggerReading" or "EventReadings" or "RecoveryReadings")
            .Select(x =>
            {
                var fact = x.Kind switch
                {
                    "TriggerReading" => x.Facts.FirstOrDefault(f => f.StartsWith("原始触发样本值", StringComparison.Ordinal)),
                    "EventReadings" when trigger != null => x.Facts.FirstOrDefault(f => f.Contains(trigger.ResourceId.ToString(), StringComparison.Ordinal)),
                    "RecoveryReadings" when recovery != null => x.Facts.FirstOrDefault(f => f.Contains("有效时间 " + recovery, StringComparison.Ordinal)),
                    _ => null
                } ?? x.Facts.FirstOrDefault();
                return fact == null ? null : new InvestigationObservation(fact, [x.Id]);
            }).OfType<InvestigationObservation>();
        return core.Concat(raw).GroupBy(x => x.Text, StringComparer.Ordinal)
            .Select(group => new InvestigationObservation(group.Key, group.SelectMany(x => x.EvidenceIds).Distinct(StringComparer.Ordinal).Take(8).ToArray()))
            .Take(8).ToArray();
    }

    private static string ComposeSummary(IReadOnlyList<InvestigationEvidence> ledger)
    {
        var summary = ledger.Where(x => x.Kind == "Alarm").SelectMany(x => x.Facts.Take(2))
            .Concat(ledger.Where(x => x.Kind == "Trend").SelectMany(x => x.Facts.Where(f => f.Contains("最小值为", StringComparison.Ordinal)).Take(1)));
        var composed = string.Join('\n', summary.Take(3));
        return composed.Length is > 0 and <= 2000 ? composed : SafeSummary;
    }

    public async Task<InvestigationExecutionResult> ExecuteAsync(IReadOnlyList<InvestigationEvidence> evidence,
        Func<string, JsonElement, CancellationToken, Task<InvestigationToolResult>> readTool,
        Func<CancellationToken, Task> authorize, LlmBudget budget, CancellationToken cancellationToken)
    {
        var ledger = evidence.ToList();
        var inputLimit = budget.RemainingInputTokens >= 28000 ? 16000 : 8000;
        var renderFacts = options.Value.ServerRenderedFacts;
        var instructions = renderFacts || budget.RemainingInputTokens >= 28000 ? Instructions : CompactInstructions;
        ModelEvidence[] initialEvidence;
        List<LlmMessage> messages;
        while (true)
        {
            initialEvidence = ForModel(ledger, inputLimit);
            messages =
            [
                new(LlmRole.System, InvestigationContract.SystemPrompt + instructions),
                new(LlmRole.User, JsonSerializer.Serialize(new { mode = PromptVersion, factRendering = renderFacts ? "server" : "model", requiredObservations = RequiredObservations(ledger), evidence = initialEvidence }, Json))
            ];
            // Include schema, tools and protected facts in the input reservation,
            // preserving smaller configured budgets instead of raising them.
            var initialRequest = new LlmRequest(messages, Tools, InvestigationContract.OutputSchema, MaxOutputTokens: budget.RemainingOutputTokens);
            var overhead = ChatCompletionsProtocol.Serialize(initialRequest, string.IsNullOrWhiteSpace(options.Value.Model) ? "fake" : options.Value.Model,
                options.Value.ReasoningEffort, options.Value.StreamResponses).Length + 512 - budget.RemainingInputTokens;
            if (overhead <= 0 || inputLimit <= 1024)
            {
                break;
            }
            inputLimit = Math.Max(1024, inputLimit - overhead - 128);
        }
        var fullySupplied = initialEvidence.Where(x => x.OmittedFactCount == 0).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var suppliedWarnings = ledger.Where(x => x.Kind == "DataQuality").SelectMany(x => x.Facts).ToHashSet(StringComparer.Ordinal);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Deadline);
        while (true)
        {
            budget.CheckDeadline();
            if (budget.RemainingOutputTokens <= 0)
            {
                throw new LlmException(LlmFailure.BudgetExceeded);
            }
            await authorize(deadline.Token);
            var completion = await gateway.CompleteAsync(new(messages, Tools, InvestigationContract.OutputSchema,
                MaxOutputTokens: budget.RemainingOutputTokens), budget, deadline.Token);
            await authorize(deadline.Token);
            if (completion.ToolCalls.Count == 0)
            {
                if (completion.Content is null)
                {
                    throw new LlmException(LlmFailure.InvalidResponse);
                }
                InvestigationResult result;
                try
                {
                    var content = completion.Content;
                    if (renderFacts)
                    {
                        // Validate the model's original reference set before composing
                        // anything. Unexpected model observations are rejected, never
                        // silently dropped in order to turn a failed answer into success.
                        var draft = InvestigationContract.ParseAndValidateReferences(content, ledger.Select(x => x.Id).ToHashSet(StringComparer.Ordinal), logger, deriveMissingReferences: true);
                        if (draft.Observations.Length != 0 || draft.Summary != SafeSummary)
                        {
                            logger?.LogWarning("AI answer invalid: rule=server-facts-draft-shape");
                            throw new LlmException(LlmFailure.InvalidResponse);
                        }
                        if (draft.NextChecks.Length == 0)
                        {
                            logger?.LogWarning("AI answer invalid: rule=missing-actionable-checks");
                            throw new LlmException(LlmFailure.InvalidResponse);
                        }
                        content = InvestigationContract.Serialize(draft with
                        {
                            Observations = RequiredObservations(ledger),
                            Summary = ComposeSummary(ledger),
                            MissingInformation = ledger.Where(x => x.Kind == "DataQuality").SelectMany(x => x.Facts)
                                .Where(f => f.StartsWith("调查取数范围：", StringComparison.Ordinal))
                                .Concat(ledger.Where(x => x.Kind is "EventReadings" or "RecoveryReadings")
                                    .Where(x => x.Facts.Any(f => f.Contains("不可查询", StringComparison.Ordinal)))
                                    .Select(x => x.Kind == "EventReadings"
                                        ? "触发事件的原始观测不可查询；历史快照和聚合不能替代完整原始过程。"
                                        : "恢复事件的原始观测不可查询；需要保留无法核实的范围及责任接续。"))
                                .Concat(draft.MissingInformation).Distinct(StringComparer.Ordinal).ToArray(),
                            EvidenceIds = null!
                        });
                    }
                    result = ValidateFacts(content, ledger, logger);
                }
                catch (LlmException exception)
                {
                    logger?.LogWarning("AI investigation failed: {Failure}; stage=structured-facts-validation; evidence={EvidenceCount}",
                        exception.Failure, ledger.Count);
                    throw;
                }
                return new(InvestigationContract.Serialize(result), completion.Model, options.Value.Provider == "Fake");
            }
            messages.Add(new(LlmRole.Assistant, completion.Content, completion.ToolCalls));
            foreach (var call in completion.ToolCalls)
            {
                if (call.Arguments.Length > 1024)
                {
                    throw new LlmException(LlmFailure.InvalidRequest);
                }
                JsonElement arguments;
                try
                {
                    arguments = JsonDocument.Parse(call.Arguments, new JsonDocumentOptions { MaxDepth = 4 }).RootElement.Clone();
                }
                catch (JsonException) { throw new LlmException(LlmFailure.InvalidRequest); }
                ValidateTool(call.Name, arguments);
                await authorize(deadline.Token);
                using var toolTimeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                toolTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var tool = await readTool(call.Name, arguments, toolTimeout.Token);
                var existingIds = tool.Evidence.Where(x => fullySupplied.Contains(x.Id)).Select(x => x.Id).ToArray();
                var newEvidence = ForModel(tool.Evidence.Where(x => !fullySupplied.Contains(x.Id)), 8000);
                foreach (var item in newEvidence.Where(x => x.OmittedFactCount == 0))
                {
                    fullySupplied.Add(item.Id);
                }
                foreach (var item in tool.Evidence)
                {
                    if (!ledger.Any(x => x.Id == item.Id))
                    {
                        ledger.Add(item);
                    }
                }
                if (ledger.Count > 40)
                {
                    throw new LlmException(LlmFailure.BudgetExceeded);
                }
                await authorize(deadline.Token);
                messages.Add(new(LlmRole.Tool, JsonSerializer.Serialize(new
                {
                    evidence = newEvidence,
                    existingEvidenceIds = existingIds,
                    warnings = tool.Warnings.Where(suppliedWarnings.Add).ToArray()
                }, Json), ToolCallId: call.Id));
            }
        }
    }

    internal sealed record ModelEvidence(string Id, string Kind, string Title, string[] Facts, int OmittedFactCount);

    internal static ModelEvidence[] ForModel(IEnumerable<InvestigationEvidence> evidence, int maximumBytes)
    {
        // Keep the immutable full ledger for expansion. Pack complete facts, never cut a manual
        // mid-sentence. This also keeps a large document/quality ledger within the existing budget.
        var items = evidence.OrderBy(x => x.Kind switch
        {
            "Alarm" => 0,
            "Asset" => 1,
            "EventTrend" => 2,
            "RecoveryTrend" => 3,
            "Trend" => 4,
            "TriggerReading" => 5,
            "EventReadings" => 6,
            "RecoveryReadings" => 7,
            "DataQuality" => 8,
            "Maintenance" => 9,
            "Document" => 11,
            _ => 10
        }).ToArray();
        var packed = items.Select(x => new ModelEvidence(x.Id, x.Kind, x.Title, [], x.Facts.Length)).ToArray();
        var candidates = items.SelectMany((item, index) => item.Facts.Select((fact, round) => new
        {
            Index = index,
            Fact = fact,
            Priority = item.Kind switch
            {
                "Document" => 100 + round,
                _ => round * 15 + index
            }
        })).OrderBy(x => x.Priority);
        foreach (var candidate in candidates)
        {
            var before = packed[candidate.Index];
            packed[candidate.Index] = before with
            {
                Facts = [.. before.Facts, candidate.Fact],
                OmittedFactCount = before.OmittedFactCount - 1
            };
            if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(packed, Json)) > maximumBytes)
            {
                packed[candidate.Index] = before;
            }
        }
        return packed;
    }
}

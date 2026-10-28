using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using PlantOps.Application.AI;

namespace PlantOps.AI;

public sealed class InvestigationEngine(ILlmGateway gateway, IOptions<AiOptions> options,
    ILogger<InvestigationEngine>? logger = null) : IInvestigationEngine
{
    public const string SafeSummary = "调查已完成；结论仍需现场验证。";
    public const string EnglishSafeSummary = "Investigation completed; conclusions still require on-site verification.";
    public const string PromptVersion = "investigation-extractive-v11";
    private const string EnglishInstructions = "\nRespond in English. Keep JSON property names and enum values unchanged. "
        + "All newly written hypotheses, checks and missingInformation MUST be English. Original evidence, observations and hypothesis basis MUST remain verbatim in their source language; never translate or paraphrase a quoted fact. "
        + "If factRendering=server, return observations=[] and summary EXACTLY: " + EnglishSafeSummary
        + " If factRendering=model, observations must equal requiredObservations EXACTLY and summary must be EXACTLY: " + EnglishSafeSummary
        + " Keep trigger, recovery and current evidence separate. Current normal never replaces the historical event. "
        + "Hypotheses are optional, default [] unless an observed fact distinguishes a cause. A short excursion, a manual alone or missing data is not a cause. "
        + "Prefix hypotheses with Possible or To verify, use weak/moderate support, copy ONE ENTIRE observed fact exactly into basis, state limitations and a distinguishing read-only verification. "
        + "Analyze supplied RelatedTrend and RelatedComparison values and quality before proposing checks. Never ask humans to retrieve these already supplied trends or call them missing. "
        + "Same-asset candidates do not establish a physical relation; completed-minute extrema do not prove sub-minute synchrony or causation. "
        + "nextChecks MUST contain two to four targeted read-only checks, including in server rendering mode. Each needs a task, specific evidence to record, a verifiable completion condition and escalation condition with responsible role. "
        + "Completion must describe when all checks and required review are complete, never a status such as Not completed or Pending evidence. "
        + "Known weak maintenance records are not missing records. omittedFactCount means facts exist but were omitted from input; never claim they are absent. "
        + "If raw history is explicitly unavailable, use retained alarm/minute evidence and document retention limits and responsible review; never require reconstruction of lost samples or claim missing history was verified. "
        + "Only ENTIRE copied cited facts may contain numbers, timestamps, units or equipment IDs. No controls, adjustments, commands or parameter changes, even in negations. "
        + "Sampling, KeepAlive and constant values prove neither fault nor notification rate. Never claim root cause or asset safety. "
        + "missingInformation has no citations: describe genuinely missing evidence without digits, timestamps, equipment IDs or control-operation words, even in negations. Use the alarm asset, alarm point or related point instead of identifiers. "
        + "dataWarnings=[]; omit root evidenceIds. existingEvidenceIds means fully supplied; query only missing data.";

    private const string AssessmentInstructions = "\nAssessment and PointStatus are server-computed, authoritative labels scoped to CurrentReadingsAtAsOf. "
        + "Do not rewrite the labels. Keep current assessments separate from historical event/windows. A bad temperature point does not negate trusted vibration evidence. "
        + "Present means observed threshold evidence, not duration, root cause or safety. NotFound is limited to inspected points and enabled thresholds. "
        + "Source health/KeepAlive is not measurement freshness; silence in Changes mode does not prove sensor failure. Read the supplied point reasons and acknowledge unknown identity/coverage.";

    private const string Instructions = "\nChinese. If factRendering=server, return observations=[] and summary EXACTLY 调查已完成；结论仍需现场验证。 The server renders authoritative observations and factual summary; do not copy them into the response. If factRendering=model, observations must equal requiredObservations EXACTLY without paraphrasing. Current normal never replaces the event. "
        + "Summary copies up to three observation sentences joined with newline: trigger, recovery, separate current; or exactly: 调查已完成；结论仍需现场验证。 "
        + "Hypotheses OPTIONAL; default [] unless an observed fact distinguishes a cause. Restating a short vibration excursion, a manual alone, or missing data is NOT a cause hypothesis. Prefix 可能/待验证, weak/moderate; basis must COPY ONE ENTIRE observed Fact character-for-character, never summarize several facts; limitations=what cannot be concluded; verification=discriminating read-only check. "
        + "RelatedTrend and RelatedComparison are automatically queried same-asset candidate trends with aligned UTC event/recovery/current windows. Analyze their actual supplied values and quality before proposing checks. Never task humans to fetch these already supplied trends or mark them unprovided. Limit follow-up to explicit unavailable windows, external operating conditions, physical mapping or remaining uncertainty. Completed-minute extrema do not prove sub-minute synchrony or cause. Same asset is not proof of physical relation. "
        + "nextChecks MUST contain two to four targeted read-only checks, also in server rendering mode; empty nextChecks is rejected. Each: text=task, record=specific evidence to record, completion=verifiable acceptance condition (全部…且…后完成), escalation=condition and responsible role. Never use 未完成/部分完成/待补证 as completion. Reuse supplied facts; do not task humans to re-fetch them. "
        + "Known but weak maintenance records are not missing records. omittedFactCount means facts exist in the ledger but were not supplied due to input limits; NEVER call them absent/empty/unqueryable. MissingInformation names only genuinely missing evidence, not unacknowledged alarm state. "
        + "If raw history is explicitly unqueryable, use retained alarm/minute evidence; record the limit and retention/handoff review. Do not demand reconstruction of lost samples as completion; allow documented unavailability plus responsible review, without claiming the missing history was verified. "
        + "Numbers/IDs only inside entire cited Facts in observations, hypotheses and nextChecks. Outside copied facts avoid 设置/设定/改为/调节/调整/写入/停机/开机/启动/停止/复位/修改阈值 even in negations. Demos lack field authority. Sampling, KeepAlive and constant values prove no fault or notification rate. "
        + "missingInformation: describe missing evidence without digits, timestamps, equipment IDs or control-operation words, even in negations. Use 告警设备/告警点位/关联测点 instead of IDs. Do not copy numbered facts here because this field has no citations. "
        + "dataWarnings=[]; omit root evidenceIds. existingEvidenceIds=fully supplied; query only missing data.";

    private const string CompactInstructions = "\nChinese. Copy requiredObservations exactly; event/current separate. Summary=observation or 调查已完成；结论仍需现场验证。 Optional hypotheses: 可能/待验证, weak/moderate, entire observed basis, limitations, distinguishing verification. No manual-only causes. Use supplied RelatedTrend/RelatedComparison before checks; do not request them again. Checks cite facts: record, completion condition, escalation. Only whole cited facts may contain numbers/IDs; no controls. missingInformation has no citations: no digits, timestamps, equipment IDs or control-operation words even in negations; use 告警设备/告警点位/关联测点. Input omissions are not data loss. dataWarnings=[]; omit root evidenceIds. existingEvidenceIds=fully supplied.";

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
        ILogger? logger = null, string language = "zh-CN")
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
        var assessment = InvestigationAssessmentPolicy.FromEvidence(evidence);
        if (assessment != null)
        {
            var advice = result.Hypotheses.SelectMany(x => new[] { x.Text, x.Verification, x.Limitations ?? "" })
                .Concat(result.NextChecks.SelectMany(x => new[] { x.Text, x.Record ?? "", x.Completion ?? "", x.Escalation ?? "" }))
                .Concat(result.MissingInformation);
            foreach (var text in advice)
            {
                var data = Regex.Match(text, @"当前数据可信度[：:]\s*(部分可信|不可验证|可信)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                var anomaly = Regex.Match(text, @"当前设备异常证据[：:]\s*(证据不足|未发现|存在)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                var englishData = Regex.Match(text, @"current data trust\s*:\s*(partially trusted|unverifiable|trusted)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
                var englishAnomaly = Regex.Match(text, @"current (?:asset )?anomaly evidence\s*:\s*(insufficient|not found|present)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
                var expectedTrust = assessment.DataTrust switch
                {
                    "Trusted" => "trusted",
                    "Partial" => "partially trusted",
                    _ => "unverifiable"
                };
                var expectedAnomaly = assessment.AnomalyEvidence switch
                {
                    "Present" => "present",
                    "NotFound" => "not found",
                    _ => "insufficient"
                };
                if (data.Success && data.Groups[1].Value != InvestigationAssessmentPolicy.TrustLabel(assessment.DataTrust)
                    || anomaly.Success && anomaly.Groups[1].Value != InvestigationAssessmentPolicy.AnomalyLabel(assessment.AnomalyEvidence)
                    || englishData.Success && !englishData.Groups[1].Value.Equals(expectedTrust, StringComparison.OrdinalIgnoreCase)
                    || englishAnomaly.Success && !englishAnomaly.Groups[1].Value.Equals(expectedAnomaly, StringComparison.OrdinalIgnoreCase)
                    || Regex.IsMatch(text, @"(?:已确认|证明|保证|说明|判定|确认)(?:全设备|设备)(?:完全|绝对|运行)?(?:正常|安全|无异常)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                {
                    throw new LlmException(LlmFailure.InvalidResponse);
                }
            }
        }
        if (result.Summary != (language == "en" ? EnglishSafeSummary : SafeSummary) && !result.Observations.Any(x => x.Text == summary) && (summaryFacts.Length is < 1 or > 3
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
        string WithoutQuotedFacts(string text, IEnumerable<string> ids)
        {
            // A repeated number is not sufficient grounding: only complete, cited facts may carry
            // numbers. The remaining connective advice must contain no numeric claims or parameters.
            foreach (var fact in ids.SelectMany(id => ledger[id].Facts).OrderByDescending(x => x.Length))
            {
                text = text.Replace(fact, "", StringComparison.Ordinal);
            }
            return text;
        }
        bool UnsupportedNumbers(string text, IEnumerable<string> ids) => Regex.IsMatch(WithoutQuotedFacts(text, ids),
            @"\p{N}|设置|设定|改为|调节|调整|写入|停机|开机|启动|停止|复位|修改阈值|\b(?:adjust|write|shutdown|restart|reset|reconfigure|activate|deactivate)\b|\b(?:shut\s*down|turn\s+(?:on|off))\b|\b(?:set|change|start|stop)\b.{0,30}\b(?:equipment|asset|pump|motor|machine|device|valve|threshold|setpoint|parameter|output)\b|\b(?:confirmed?|proven?|guaranteed?|definite|certain)\s+(?:root\s+)?cause\b|\b(?:asset|equipment|device)\s+(?:is|are)\s+(?:(?:completely|fully|definitely)\s+)?(?:safe|normal|healthy|fault.free)\b",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        bool WrongLanguage(string text, IEnumerable<string> ids) => language == "en"
            && Regex.IsMatch(WithoutQuotedFacts(text, ids), @"\p{IsCJKUnifiedIdeographs}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        LlmException Invalid(string rule, int index)
        {
            logger?.LogWarning("AI answer invalid: rule={Rule}; item={Item}", rule, index);
            return new LlmException(LlmFailure.InvalidResponse, validationRule: rule);
        }
        void CheckAvailability(string text, string field, int index)
        {
            if (field == "check-escalation")
            {
                // An escalation antecedent describes a contingency, not the
                // current evidence. Remove only the explicitly delimited
                // antecedent; assertions in its consequence or later clauses
                // must still be checked against the full ledger.
                text = Regex.Replace(text,
                    @"(^|[。；;\r\n])\s*(?:如果|若|如|当|一旦|if\b|when\b|once\b)[^，,。；;\r\n]*?(?:，|,|则|就)",
                    "$1", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            }
            bool Match(string pattern) => Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            // An input omission is not a missing measurement. Check against the
            // full authorized ledger, never just the model's packed projection.
            var mentionsRecovery = text.Contains("恢复", StringComparison.Ordinal) || Match(@"\brecovery\b");
            var mentionsTrigger = text.Contains("触发", StringComparison.Ordinal) || Match(@"\btrigger\b");
            var mentionsEvent = text.Contains("事件", StringComparison.Ordinal) || Match(@"\bevent\b");
            var hasRaw = evidence.Any(e => e.Kind is "EventReadings" or "RecoveryReadings"
                && (!mentionsRecovery || mentionsTrigger || e.Kind == "RecoveryReadings")
                && (!mentionsEvent && !mentionsTrigger || mentionsRecovery || e.Kind == "EventReadings")
                && e.Facts.Any(f => !f.Contains("不可查询", StringComparison.Ordinal) && !f.Contains("未查询到", StringComparison.Ordinal)));
            var asksForCompleteRaw = Match(@"(?:完整|全量|全部|连续|更长时段).{0,8}原始(?:观测|样本|读数|数据)|\b(?:complete|full|all|continuous|longer).{0,30}\braw\s+(?:observations|samples|readings|data)\b");
            if (hasRaw && !asksForCompleteRaw && Match(@"原始(?:观测|样本|读数|数据)(?:有界)?(?:节选)?(?:为空|缺失|不可查询|未提供|不存在)|(?:缺少|缺失|没有|未提供).{0,12}原始(?:观测|样本|读数|数据)|\braw\s+(?:observations|samples|readings|data)\s+(?:(?:are|is|were|was)\s+)?(?:missing|empty|unavailable|absent|not\s+(?:provided|available|queryable))\b|\b(?:missing|no|lack\s+of|absence\s+of)\s+(?:event\s+|recovery\s+)?raw\s+(?:observations|samples|readings|data)\b"))
            {
                throw Invalid(field + "-contradicts-raw-evidence", index);
            }
            if (evidence.Any(e => e.Kind == "Maintenance" && e.Facts.Any(f => f.StartsWith("维护记录：", StringComparison.Ordinal)))
                && Match(@"维护记录(?:事实)?(?:缺失|为空|不存在)|(?:缺少|缺失|没有)维护记录(?:事实)?[。；，,;]?$|\bmaintenance\s+records\s+(?:are\s+)?(?:missing|empty|absent|unavailable)\b|\b(?:missing|no|lack\s+of)\s+maintenance\s+records\b"))
            {
                throw Invalid(field + "-contradicts-maintenance", index);
            }
        }
        for (var i = 0; i < result.Hypotheses.Length; i++)
        {
            var x = result.Hypotheses[i];
            var tentative = language == "en"
                ? x.Text.StartsWith("Possible", StringComparison.OrdinalIgnoreCase) || x.Text.StartsWith("To verify", StringComparison.OrdinalIgnoreCase)
                : x.Text.StartsWith("可能", StringComparison.Ordinal) || x.Text.StartsWith("待验证", StringComparison.Ordinal);
            if (!tentative
            || x.Support == "strong" || x.Text.Contains("已确认", StringComparison.Ordinal)
            || x.EvidenceIds.All(id => ledger[id].Kind is "DataQuality" or "Document" or "Asset" or "Assessment" or "PointStatus")
            || string.IsNullOrWhiteSpace(x.Basis) || x.Basis.Length > 2000
            || x.Basis.Contains("不可查询", StringComparison.Ordinal) || x.Basis.Contains("未查询到", StringComparison.Ordinal) || x.Basis.Contains("无可用分钟聚合", StringComparison.Ordinal)
            || !x.EvidenceIds.Any(id => ledger[id].Kind is not ("DataQuality" or "Document" or "Asset" or "Assessment" or "PointStatus") && ledger[id].Facts.Contains(x.Basis, StringComparer.Ordinal)))
            {
                throw Invalid("hypothesis-observed-basis", i);
            }
            foreach (var (field, text) in new[] { ("text", x.Text), ("limitations", x.Limitations), ("verification", x.Verification) })
            {
                if (string.IsNullOrWhiteSpace(text) || text.Length > 2000 || UnsupportedNumbers(text, x.EvidenceIds) || WrongLanguage(text, x.EvidenceIds))
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
                if (string.IsNullOrWhiteSpace(text) || text.Length > (field == "text" ? 2000 : 500) || UnsupportedNumbers(text, x.EvidenceIds) || WrongLanguage(text, x.EvidenceIds))
                {
                    throw Invalid("check-" + field + "-advice", i);
                }
                CheckAvailability(text, "check-" + field, i);
            }
            if (Regex.IsMatch(x.Completion!, @"^\s*(?:未完成|部分完成|待补证|待核查|not completed|partially complete[d]?|pending evidence|pending review)(?:[；;，,。.]|$)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
            {
                throw Invalid("check-completion-is-status", i);
            }
        }
        for (var i = 0; i < result.MissingInformation.Length; i++)
        {
            var serverScopeFact = result.MissingInformation[i].StartsWith("调查取数范围：", StringComparison.Ordinal)
                && evidence.Any(e => e.Kind == "DataQuality" && e.Facts.Contains(result.MissingInformation[i], StringComparer.Ordinal));
            if (!serverScopeFact && (UnsupportedNumbers(result.MissingInformation[i], []) || WrongLanguage(result.MissingInformation[i], [])))
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
        var hasComparison = items.Any(x => x.Kind == "RelatedComparison");
        var triggerInRaw = trigger != null && items.Any(x => x.Kind == "EventReadings" && x.Facts.Any(f => f.Contains(trigger.ResourceId.ToString(), StringComparison.Ordinal)));
        var raw = items.Where(x => x.Kind is "TriggerReading" or "EventReadings" or "RecoveryReadings")
            .Where(x => !hasComparison || !triggerInRaw || x.Kind != "TriggerReading")
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
        return core.Concat(raw).Concat(items.Where(x => x.Kind == "RelatedComparison").SelectMany(x => x.Facts.Select(f => new InvestigationObservation(f, [x.Id]))))
            .GroupBy(x => x.Text, StringComparer.Ordinal)
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
        Func<CancellationToken, Task> authorize, LlmBudget budget, CancellationToken cancellationToken, string language = "zh-CN")
    {
        if (language is not ("zh-CN" or "en"))
        {
            throw new LlmException(LlmFailure.InvalidRequest);
        }
        var ledger = evidence.ToList();
        var inputLimit = budget.RemainingInputTokens >= 28000 ? 16000 : 8000;
        var renderFacts = options.Value.ServerRenderedFacts;
        var safeSummary = language == "en" ? EnglishSafeSummary : SafeSummary;
        var instructions = language == "en" ? EnglishInstructions
            : renderFacts || budget.RemainingInputTokens >= 28000 ? Instructions : CompactInstructions;
        ModelEvidence[] initialEvidence;
        List<LlmMessage> messages;
        while (true)
        {
            initialEvidence = ForModel(ledger, inputLimit);
            messages =
            [
                new(LlmRole.System, InvestigationContract.SystemPrompt + (ledger.Any(x => x.Kind == "Assessment") ? AssessmentInstructions : "") + instructions),
                new(LlmRole.User, JsonSerializer.Serialize(new { mode = PromptVersion, language, factRendering = renderFacts ? "server" : "model", requiredObservations = RequiredObservations(ledger), evidence = initialEvidence }, Json))
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
        var correctionAttempted = false;
        while (true)
        {
            budget.CheckDeadline();
            if (budget.RemainingOutputTokens <= 0)
            {
                throw new LlmException(LlmFailure.BudgetExceeded);
            }
            await authorize(deadline.Token);
            var request = new LlmRequest(messages, Tools, InvestigationContract.OutputSchema,
                MaxOutputTokens: budget.RemainingOutputTokens, DisableToolCalls: correctionAttempted);
            string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
            var selected = options.Value;
            await budget.RecordRequestAsync(PromptVersion, JsonSerializer.SerializeToElement(new
            {
                promptVersion = PromptVersion,
                language,
                systemPromptSha256 = Hash(messages[0].Content!),
                schemaName = InvestigationContract.OutputSchema.Name,
                schemaSha256 = Hash(InvestigationContract.OutputSchema.Schema.GetRawText()),
                toolsSha256 = Hash(JsonSerializer.Serialize(Tools, Json)),
                evidenceSha256 = Hash(JsonSerializer.Serialize(ledger, new JsonSerializerOptions(JsonSerializerDefaults.Web))),
                requestSha256 = Convert.ToHexStringLower(SHA256.HashData(ChatCompletionsProtocol.Serialize(request,
                    selected.Provider == "Fake" ? "fake" : selected.Model,
                    selected.Provider == "Fake" ? null : selected.ReasoningEffort,
                    selected.Provider != "Fake" && selected.StreamResponses))),
                trustPolicyVersion = InvestigationAssessmentPolicy.Version + "/authorized-ledger-fact-validation-v9",
                evidencePolicyVersion = "event-recovery-current-related-v1",
                serverRenderedFacts = renderFacts,
                provider = selected.Provider,
                model = selected.Provider == "Fake" ? "fake" : selected.Model,
                reasoningEffort = selected.Provider == "Fake" ? null : selected.ReasoningEffort,
                streamResponses = selected.Provider != "Fake" && selected.StreamResponses,
                selected.RequestTimeoutSeconds,
                selected.MaxRetries,
                request.MaxOutputTokens,
                validationCorrection = correctionAttempted
            }, Json), deadline.Token);
            var completion = await gateway.CompleteAsync(request, budget, deadline.Token);
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
                        if (draft.Observations.Length != 0 || draft.Summary != safeSummary)
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
                            Summary = language == "en" ? EnglishSafeSummary : ComposeSummary(ledger),
                            MissingInformation = ledger.Where(x => x.Kind == "DataQuality").SelectMany(x => x.Facts)
                                .Where(f => f.StartsWith("调查取数范围：", StringComparison.Ordinal))
                                .Concat(ledger.Where(x => x.Kind is "EventReadings" or "RecoveryReadings")
                                    .Where(x => x.Facts.Any(f => f.Contains("不可查询", StringComparison.Ordinal)))
                                    .Select(x => x.Kind == "EventReadings"
                                        ? language == "en" ? "Raw observations for the trigger event are unavailable; historical snapshots and aggregates do not replace the complete raw process." : "触发事件的原始观测不可查询；历史快照和聚合不能替代完整原始过程。"
                                        : language == "en" ? "Raw observations for the recovery event are unavailable; document the unverifiable scope and responsible handoff." : "恢复事件的原始观测不可查询；需要保留无法核实的范围及责任接续。"))
                                .Concat(draft.MissingInformation).Distinct(StringComparer.Ordinal).ToArray(),
                            EvidenceIds = null!
                        });
                    }
                    result = ValidateFacts(content, ledger, logger, language);
                }
                catch (LlmException exception)
                {
                    // Correct only this known wording failure, once, within the original
                    // run budget. The replacement passes the entire validator again.
                    if (!correctionAttempted && exception.ValidationRule == "missing-information-advice"
                        && budget.ModelRounds < budget.Limits.MaxModelRounds && budget.RemainingOutputTokens > 0)
                    {
                        var correctionMessages = new List<LlmMessage>(messages)
                        {
                            new(LlmRole.Assistant, completion.Content),
                            new(LlmRole.User, "The previous answer was rejected: rule=missing-information-advice. Return the complete corrected JSON. "
                                + "missingInformation has no citations: describe only genuinely missing evidence, without any digits, timestamps, equipment IDs or control-operation words (including in negations). "
                                + (language == "en" ? "Write English; use the alarm asset, alarm point or related point. " : "Use 告警设备、告警点位、关联测点. ")
                                + "Do not delete a genuine limitation merely to pass validation. Keep the other grounded fields and server fact-rendering rules. No tool calls.")
                        };
                        var correctionRequest = new LlmRequest(correctionMessages, Tools, InvestigationContract.OutputSchema,
                            MaxOutputTokens: budget.RemainingOutputTokens, DisableToolCalls: true);
                        if (ChatCompletionsProtocol.Serialize(correctionRequest,
                            selected.Provider == "Fake" ? "fake" : selected.Model,
                            selected.Provider == "Fake" ? null : selected.ReasoningEffort,
                            selected.Provider != "Fake" && selected.StreamResponses).Length + 256 <= budget.RemainingInputTokens)
                        {
                            logger?.LogWarning("AI answer correction requested: rule={Rule}; maximumCorrections=1", exception.ValidationRule);
                            messages = correctionMessages;
                            correctionAttempted = true;
                            continue;
                        }
                    }
                    logger?.LogWarning("AI investigation failed: {Failure}; stage=structured-facts-validation; evidence={EvidenceCount}",
                        exception.Failure, ledger.Count);
                    throw;
                }
                return new(InvestigationContract.Serialize(result), completion.Model, options.Value.Provider == "Fake");
            }
            if (correctionAttempted)
            {
                throw new LlmException(LlmFailure.InvalidResponse);
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
            "Assessment" => 0,
            "PointStatus" => 1,
            "Asset" => 1,
            "EventTrend" => 2,
            "RecoveryTrend" => 3,
            "Trend" => 4,
            "TriggerReading" => 5,
            "EventReadings" => 6,
            "RecoveryReadings" => 7,
            "DataQuality" => 8,
            "Maintenance" => 9,
            "RelatedComparison" => 10,
            "RelatedTrend" => 10,
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

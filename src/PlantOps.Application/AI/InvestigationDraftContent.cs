using System.Text;

namespace PlantOps.Application.AI;

public static class InvestigationDraftContent
{
    public static WorkOrderDraft Build(InvestigationAdvice advice, string language, bool requireDraft = true)
    {
        if (requireDraft && (advice.WorkOrder.Status != "DraftReady" || advice.WorkOrder.Draft == null))
        {
            throw new InvalidOperationException("This investigation does not offer a work-order draft.");
        }

        var english = language == "en";
        string L(string zh, string en) => english ? en : zh;
        string Label(string value) => value switch
        {
            "Ready" => L("建议已可用", "Advice ready"),
            "NeedsMoreEvidence" => L("需要补充关键证据", "More evidence needed"),
            "NeedsExpertReview" => L("需要专家复核", "Expert review needed"),
            "AlarmEvent" => L("告警事件", "Alarm event"),
            "Recovery" => L("恢复阶段", "Recovery"),
            "Current" => L("调查时当前快照", "Current investigation snapshot"),
            "EquipmentCondition" => L("设备状态", "Equipment condition"),
            "MeasurementChain" => L("测量链", "Measurement chain"),
            "AnomalySupported" => L("有异常证据", "Anomaly evidence present"),
            "NoAnomalyFound" => L("本检查范围未发现异常", "No anomaly found in this scope"),
            "Inconclusive" => L("证据不足", "Inconclusive"),
            "MaintenanceEngineer" => L("维护工程师", "Maintenance engineer"),
            "Operator" => L("操作人员", "Operator"),
            "Weak" => L("有限支持", "Weak support"),
            "Moderate" => L("中等支持", "Moderate support"),
            _ => value
        };
        var plan = advice.WorkOrder.Draft;
        var text = new StringBuilder();
        text.AppendLine(L("人工审核草稿；任务执行前由维护人员核实适用规程与现场条件。", "Draft for human review; maintenance personnel verify applicable procedures and field conditions before work."));
        text.AppendLine(advice.Summary);
        text.AppendLine(L("建议状态：", "Advice status: ") + Label(advice.Status));
        text.AppendLine(advice.StatusReason);
        if (plan != null)
        {
            text.AppendLine(L("任务目标：", "Objective: ") + plan.Objective);
            text.AppendLine(L("优先级依据：", "Priority rationale: ") + plan.PriorityReason);
        }
        text.AppendLine(L("调查发现（仅限所列对象与时间范围）", "Findings (limited to the listed subjects and time scopes)"));
        foreach (var finding in advice.Findings)
        {
            text.AppendLine($"[{finding.Id}] {Label(finding.Scope)} / {Label(finding.Domain)} / {Label(finding.Status)}: {finding.Interpretation} "
                + string.Join(" ", finding.EvidenceRefs.Select(x => $"[{x}]")));
        }

        var selected = plan == null ? advice.Recommendations : plan.RecommendationIds
            .Select(id => advice.Recommendations.Single(x => x.Id == id)).ToArray();
        var selectedIds = selected.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var hypotheses = advice.Hypotheses.Where(x => x.VerificationRecommendationIds.Any(selectedIds.Contains)).ToArray();
        if (hypotheses.Length > 0)
        {
            text.AppendLine(L("待验证假设", "Hypotheses to verify"));
            foreach (var hypothesis in hypotheses)
            {
                text.AppendLine($"[{hypothesis.Id}] {Label(hypothesis.Support)}: {hypothesis.Description}");
                text.AppendLine(L("支持依据：", "Supporting evidence: ") + string.Join(" ", hypothesis.SupportingEvidenceRefs.Select(x => $"[{x}]")));
                if (hypothesis.ContradictingEvidenceRefs.Length > 0)
                {
                    text.AppendLine(L("反对依据：", "Contradicting evidence: ") + string.Join(" ", hypothesis.ContradictingEvidenceRefs.Select(x => $"[{x}]")));
                }
            }
        }
        text.AppendLine(L("按序检查与记录", "Ordered checks and records"));
        foreach (var item in selected)
        {
            text.AppendLine($"[{item.Id}] {item.Task}");
            text.AppendLine(L("依据：", "Evidence: ") + string.Join(" ", item.EvidenceRefs.Select(x => $"[{x}]")));
            text.AppendLine(L("需记录：", "Record: ") + item.Record);
            text.AppendLine(L("完成条件：", "Completion: ") + item.CompletionCriteria);
            text.AppendLine(L("升级条件：", "Escalation: ") + item.Escalation.Condition + " / " + Label(item.Escalation.ResponsibleRole));
        }
        foreach (var gap in advice.Gaps)
        {
            text.AppendLine(L("待补证据：", "Evidence gap: ") + $"[{gap.Id}] {gap.Description} {gap.WhyItMatters}");
        }

        return new(plan?.Title ?? L("告警调查结果", "Alarm investigation findings"), text.ToString(), plan?.Priority ?? "Medium");
    }
}

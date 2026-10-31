namespace PlantOps.Application.AI;

public sealed record InvestigationAdvice(string SchemaVersion, string Status, string StatusReason, string[] BlockingGapIds,
    string Summary, InvestigationFinding[] Findings, InvestigationCause[] Hypotheses,
    InvestigationRecommendation[] Recommendations, InvestigationGap[] Gaps, InvestigationWorkOrderPlan WorkOrder);

public sealed record InvestigationFinding(string Id, string[] SubjectRefs, string Scope, string Domain, string Status,
    string Interpretation, string[] EvidenceRefs);

public sealed record InvestigationCause(string Id, string Description, string Support, string[] SupportingEvidenceRefs,
    string[] ContradictingEvidenceRefs, string[] VerificationRecommendationIds);

public sealed record InvestigationRecommendation(string Id, string Kind, string[] SubjectRefs, string[] EvidenceRefs,
    string Task, string Record, string CompletionCriteria, InvestigationEscalation Escalation);

public sealed record InvestigationEscalation(string Condition, string ResponsibleRole);

public sealed record InvestigationGap(string Id, string Kind, string Description, string WhyItMatters,
    string[] EvidenceRefs, string[] ResolutionRecommendationIds);

public sealed record InvestigationWorkOrderPlan(string Status, string Reason, string[] BlockingGapIds, InvestigationDraft? Draft);

public sealed record InvestigationDraft(string Title, string Priority, string PriorityReason, string Objective, string[] RecommendationIds);

public sealed record InvestigationSubject(string Ref, string Kind, string Label);

public static class InvestigationSubjects
{
    public static string[] FindingRefs(IEnumerable<InvestigationEvidence> evidence)
    {
        var snapshot = evidence.ToArray();
        var refs = snapshot.Where(x => x.Kind == "Alarm" || x.PointStatus != null || x.DerivedTrend != null)
            .Select(x => x.ResourceId.ToString()).Distinct(StringComparer.Ordinal).ToArray();
        return refs.Length > 0 ? refs : FromEvidence(snapshot).Select(x => x.Ref).ToArray();
    }

    public static InvestigationSubject[] FromEvidence(IEnumerable<InvestigationEvidence> evidence)
    {
        var snapshot = evidence.ToArray();
        var subjects = snapshot.Where(x => x.Kind is "Asset" or "Alarm" or "PointStatus" || x.DerivedTrend != null).ToArray();
        if (subjects.Length == 0)
        {
            subjects = snapshot.Take(1).ToArray();
        }

        return subjects.GroupBy(x => x.ResourceId).Select(g => g.Last())
            .Select(x => new InvestigationSubject(x.ResourceId.ToString(), x.Kind, x.Title)).ToArray();
    }
}

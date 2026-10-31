export type AdviceRecommendation = {
  id: string
  kind: string
  subjectRefs: string[]
  evidenceRefs: string[]
  task: string
  record: string
  completionCriteria: string
  escalation: { condition: string; responsibleRole: string }
}
export type InvestigationAdvice = {
  schemaVersion: '3'
  status: 'Ready' | 'NeedsMoreEvidence' | 'NeedsExpertReview'
  statusReason: string
  blockingGapIds: string[]
  summary: string
  findings: {
    id: string
    subjectRefs: string[]
    scope: string
    domain: string
    status: string
    interpretation: string
    evidenceRefs: string[]
  }[]
  hypotheses: {
    id: string
    description: string
    support: string
    supportingEvidenceRefs: string[]
    contradictingEvidenceRefs: string[]
    verificationRecommendationIds: string[]
  }[]
  recommendations: AdviceRecommendation[]
  gaps: {
    id: string
    kind: string
    description: string
    whyItMatters: string
    evidenceRefs: string[]
    resolutionRecommendationIds: string[]
  }[]
  workOrder: {
    status: 'DraftReady' | 'NeedsInformation' | 'NotRecommended'
    reason: string
    blockingGapIds: string[]
    draft: {
      title: string
      priority: string
      priorityReason: string
      objective: string
      recommendationIds: string[]
    } | null
  }
}

# Investigation advice response v3

New investigations use `InvestigationAdvice` and the strict `investigation_v3` provider schema. The response gives engineers advice and a reviewable work-order plan. Creating or executing a work order requires the separate human approval workflow. [Response contract source](../src/PlantOps.AI/InvestigationAdviceContract.cs)

## Decisions

The service's run status (`Pending`, `Running`, `Succeeded`, `Failed`, etc.) describes execution. The AI's `status` describes the usability of its advice:

| Advice status | Meaning |
| --- | --- |
| `Ready` | A useful scoped conclusion or plan is available; root cause need not be known. |
| `NeedsMoreEvidence` | Critical gaps block a reliable assessment. `blockingGapIds` identifies them. |
| `NeedsExpertReview` | Conflicting evidence or professional limits need human judgment. |

A structurally valid incomplete answer can still produce a successful investigation run. `Ready` requires an empty `blockingGapIds` array and `NeedsMoreEvidence` requires at least one gap. The current prompt also asks for an empty array with `NeedsExpertReview`. `summary` and `statusReason` are explanations.

`workOrder.status` is independent:

| Work-order status | Payload and behavior |
| --- | --- |
| `DraftReady` | Non-null draft, no draft blockers, valid referenced recommendation IDs. Show an editable proposal. |
| `NeedsInformation` | Null draft, non-empty draft blockers. Creating a new work-order proposal returns a conflict. |
| `NotRecommended` | Null draft, no draft blockers; reason explains why no new order is warranted. |

`NeedsMoreEvidence` and `DraftReady` can coexist for diagnostic work. Root blockers concern assessment readiness; draft blockers prevent starting useful tasks, not evidence collected during those tasks. Every required explanation must be nonblank. An existing work order can still receive a reviewed investigation note. Approval, assignment, entity IDs, versions, transactions and idempotency stay server-controlled.

## Content

- `findings`: object references, `AlarmEvent` / `Recovery` / `Current` scope, `EquipmentCondition` / `MeasurementChain` domain, structured finding status, explanation and evidence references.
- `hypotheses`: optional causes, `Weak` / `Moderate` support, supporting/opposing references and verification recommendations.
- `recommendations`: typed inspection, measurement verification, evidence collection, procedure review, monitoring or escalation; each has task, record, completion criteria and escalation responsibility.
- `gaps`: missing information, insufficient detail, conflict or on-site verification; each links to a resolution recommendation.
- `workOrder.draft`: title, priority, priority rationale, objective and references to the same recommendations displayed to engineers. Task prose is generated once and reused by the draft renderer.

The draft renderer uses `Low`, `Medium` and `High` priority. The stored `DraftOrigin` remains `InvestigationTemplate`, meaning server-rendered investigation content. The investigation result and hash identify the v3 source.

## Provider schema and evidence

The active `InvestigationEngine` uses prompt `investigation-advice-v3.16`. It offers the v3 result schema and, when budget permits, read tools from the first round. After a read, if offering tools alongside the full advice schema would consume the final-answer reserve, the engine can send one smaller read-selection frame. Its input estimate plus the measured cost of the current final frame must fit the remaining input budget. It can request authorized tools or return a typed finish decision. The next round reprojects the updated evidence and checks its actual size before generating the full advice. A single-round investigation can still answer directly. Optional facts can be omitted from the request within the budget. The authorized frozen ledger remains available for inspection. [Engine source](../src/PlantOps.AI/InvestigationEngine.cs) · [Tool decisions and request traces](INVESTIGATION_DIAGNOSTICS.md)

Every provider evidence-reference array uses an enum of frozen ledger IDs. Finding targets are restricted to explicit alarm and point resources. Parent assets can be recommendation subjects. The provider sees short subject aliases issued for each request. The server resolves them and saves resource UUIDs in accepted results.

Provider finding targets are scoped by `scope`, `domain` and `status`. Current equipment findings with `AnomalySupported` or `NoAnomalyFound` require a target with a trusted latest `PointStatus`. `NoAnomalyFound` excludes targets with a current anomaly. Historical findings, measurement-chain findings and `Inconclusive` current equipment findings use the general authorized finding targets.

The model projection includes saved `recordedRecoveryAt` when available. A trend's latest segment has `spansRecoveryRef` when its endpoints strictly bracket that alarm's recorded transition. The prompt asks for supported per-point bounds and observed ranges, the saved transition and matching evidence, and an explanation of any limits on verifying the qualifying interval. A shared interval must fit all cited segments. `ReviewProcedure` is intended for a cited source with version and section or source lines. `CollectEvidence` covers missing procedures and operational records. [Evidence interpretation and review matrix](AI_INVESTIGATION_QUALITY.md)

## Validation boundaries

Server validation checks strict JSON shape, enums, cardinality, duplicate IDs and references, authorized evidence and subjects, cross-item links, blockers, draft readiness and renderable draft size. Conclusive current equipment findings must cite the target's latest trusted point snapshot. The server rejects `NoAnomalyFound` when that snapshot records a current anomaly.

A hypothesis must cite at least one supporting observational item. A document, asset summary, assessment, quality notice or empty/unavailable result alone is insufficient. The same evidence ID cannot both support and oppose a hypothesis. Determining whether observations actually support the explanation still requires semantic review.

Uncertainty, causality, procedure applicability and recommendation appropriateness are evaluated through prompting and model-quality review. Read tools stay scoped and read-only. Follow [AI Engineering Guardrails](AI_ENGINEERING_GUARDRAILS.md) when changing the engine, contract, validators or prompts.

Historical v1/v2 records keep their original format and display path. `LegacyInvestigationEngine` is reserved for compatibility and historical replay. The production service uses v3 for new executions.

## Verification

Deterministic tests cover structure, references, mixed point quality, incomplete-but-successful runs, task reuse, proposal readiness, refused tools, language, historical compatibility and budget behavior. The checked-in [semantic fixtures](../tests/fixtures/investigation-advice-semantics.json) are visible regression examples. Use contrasting phrasing and reviewer-controlled unseen cases for independent semantic evaluation.

Use the real-model runners only with current authorization and a bounded ledger. The [final-answer runner](../deploy/Test-InvestigationAdviceReal.ps1) takes `-Run <fresh-name>` and defaults to five fictional inputs and a six-send cap. `-Scenario recovery-pair -MaxRequests 2` instead checks recorded recovery versus a brief low reading with no transition. It supplies evidence directly and checks output and draft content. The script uses an existing local test build and stores reports below `artifacts/investigation-quality/`.

The [agent runner](../deploy/Test-InvestigationAgentReal.ps1) additionally checks the active engine and proposal API on a dedicated `plantops_tests` database under an isolated schema. Supply `PLANTOPS_TEST_CONNECTION`, a fresh `-Run`, `-AuthorizePaidModelCalls` and an existing `-LedgerPath` whose allowance matches `-MaxRequests`. `RecordedRecovery` and `BriefLow` seed fictional measurements, test an autonomous measurement read and stop at a Pending proposal. Use `PublishedProcedure` and `EmptyProcedure` for the matched document-search cases. Run each mode under the same authorized ledger when the allowance is shared. `-MaxRequests` must match the existing cumulative ledger limit (1–100); it does not raise the four-round ceiling per investigation. Both runners disable automatic retries. Inspect captured answers for factual grounding and task suitability.

Current acceptance is tracked in the [roadmap](../ROADMAP.md#4-p6ai-多源联合调查增强). Dated observations, regressions and workbench results are retained in the [P6 verification history](archive/P6_V3_FOLLOW_UP_20261119.md).

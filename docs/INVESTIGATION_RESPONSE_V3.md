# Investigation advice response v3

See [investigation diagnostics](INVESTIGATION_DIAGNOSTICS.md) for correlated failure traces, private model-response captures and retention limits.

New investigations use `InvestigationAdvice` and the strict `investigation_v3` provider schema in `src/PlantOps.AI/InvestigationAdviceContract.cs`. The response serves two purposes: grounded engineer advice and a reviewable work-order plan. It does not execute maintenance or create business records.

## Decisions

The service's run status (`Pending`, `Running`, `Succeeded`, `Failed`, etc.) describes execution. The AI's `status` describes the usability of its advice:

| Advice status | Meaning |
| --- | --- |
| `Ready` | A useful scoped conclusion or plan is available; root cause need not be known. |
| `NeedsMoreEvidence` | Critical gaps block a reliable assessment. `blockingGapIds` identifies them. |
| `NeedsExpertReview` | Conflicting evidence or professional limits need human judgment. |

A valid incomplete answer still produces a successful investigation run. `summary` and `statusReason` are explanations, never inputs to status parsing.

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

Subject references are issued from the frozen authorized evidence resources. Finding targets are restricted to explicit alarm/point resources; parent assets remain available as recommendation context. The provider receives these allowed finding references as an enum. AI cannot invent an asset, point or assignee. The draft renderer uses existing `Low` / `Medium` / `High` priorities. The stored `DraftOrigin` remains `InvestigationTemplate`, meaning server-rendered investigation content; the investigation result and hash identify the v3 source.

## Validation boundaries

Server validation checks strict JSON shape, enums, cardinality, duplicates, authorized evidence and subjects, cross-item links, blockers, draft readiness, renderable draft size, and contradictions with authoritative current point evidence. Affirmative current equipment conclusions must cite the latest point snapshot and respect unverifiable measurements and observed threshold anomalies; an inconclusive window finding can cite the evidence limiting that window.

Natural-language prohibitions, procedure references, uncertainty, causal reasoning and recommendation appropriateness belong to prompting and model-quality review. There is no keyword-based safety parser or self-reported `isSafe` field. A valid schema is not proof that prose is correct or safe. Read tools remain scoped, authorized and read-only regardless of model text.

Historical v1/v2 records keep their original format and display path. `LegacyInvestigationEngine` is an internal historical replay/compatibility implementation and is not registered as the production investigation service. New executions always use v3.

## Engineering guardrails for v3 changes

Follow [AI Engineering Guardrails](AI_ENGINEERING_GUARDRAILS.md) when changing this contract, the active engine, prompts or validators. Keep **typed, authoritative invariants** (scope, structured states, evidence references and current-point contradictions) separate from **semantic-quality judgments** (causality, interpretation, appropriateness and the meaning of natural-language prohibitions). Do not encode case-specific wording, keyword heuristics, or exact phrases as a proxy for correctness. A typed enum is not a diagnosis.

Before changing a validator to address a model failure, identify the invariant and its authoritative evidence source. If the failure is semantic, update the general reasoning guidance and evaluation coverage instead; consider both erroneous acceptance and erroneous rejection. Keep v1/v2 replay unchanged and preserve run/prompt version traceability. Current read-only tools, human approval and budget controls remain mandatory regardless of semantic quality.

## Verification

Deterministic tests cover structure, references, mixed point quality, incomplete-but-successful runs, task reuse, proposal readiness, forbidden tools, language, UI compatibility and budget behavior. Semantic counterexamples are retained separately in `tests/fixtures/investigation-advice-semantics.json` instead of claiming that word filters prove their safety. These visible fixtures are regression examples, **not** unseen holdouts; semantic evaluations must also review contrasting phrasing and separate reviewer-controlled cases, including false acceptance and false rejection.

`deploy/Test-InvestigationAdviceReal.ps1 -Run <fresh-name>` explicitly runs five fictional-evidence scenarios against the configured real provider. A persistent ledger caps each fresh batch at six actual sends by default (`-MaxRequests` explicitly sets a different batch cap); automatic retries are disabled. Requests, completions, token usage, failures and structured checks are retained under `artifacts/investigation-quality/`. These scenarios exercise final-answer quality and draft content, not autonomous tool selection, real protocol faults or field equipment. Final semantic acceptance requires reading the captured outputs.


The opt-in `InvestigationAdviceAgentLiveTests` additionally exercises the real investigation engine, autonomous read-tool selection and proposal API on an isolated schema in the dedicated test database. It requires `PLANTOPS_ADVICE_AGENT_REAL=1`, a preinitialized shared request ledger, `PLANTOPS_TEST_CONNECTION` and a fresh `PLANTOPS_LIVE_REPORT_ROOT`. It does not approve or execute the proposal. Raw request captures omit authorization headers; use fictional evidence only.

The active engine offers read tools and the same v3 result schema from the first round; it has no extra readiness-question round. It retains typed point facts and reuses reference-array schemas with `$defs` (supported by [OpenAI Structured Outputs](https://developers.openai.com/api/docs/guides/structured-outputs?api-mode=chat)). Later rounds reserve space for a final answer before offering more reads. Optional prose and duplicate trend detail may be omitted with explicit omission counts; the complete frozen evidence remains stored and expandable. Requests that cannot fit the configured budget still fail before HTTP.

The 2026-10-30 [real-model report](INVESTIGATION_V3_REAL_VALIDATION_20261030.md) retains failures and the distinction between direct final answers, autonomous reads, offline replay and proposal API validation. Existing 16k/2k fallback limits can be insufficient after optional reads; the workbench already configures 32k/6k. No budget is automatically raised.

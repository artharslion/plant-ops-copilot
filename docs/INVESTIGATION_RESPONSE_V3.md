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

As of 2026-11-05, `investigation-advice-v3.3` adds a shared enum of the current frozen authorized Evidence IDs to all provider-side evidence reference arrays, including hypothesis support/opposition and gap references. Gap/recommendation IDs remain separate namespaces; ID spelling does not determine the namespace. Stored v3 JSON, server validation and historical results are unchanged. Root advice readiness still requires server validation: the current flat `status`/`blockingGapIds` relationship cannot be represented using the supported Structured Outputs subset without changing the output shape. Root `anyOf` and `if`/`then`/`else`/`allOf` are unsupported; see the [official schema restrictions](https://developers.openai.com/api/docs/guides/structured-outputs#supported-schemas) and the [complete offline audit](P6_REAL_AGENT_ACCEPTANCE_20261103.md#2026-11-05-offline-schema-and-complete-response-audit). No schema-enforcement success at the configured relay is claimed without an authorized real call.

`investigation-advice-v3.4` scopes provider-side finding targets by `scope`, `domain` and `status`. Affirmative current equipment findings require a target with a trusted latest `PointStatus`. A snapshot with a current anomaly excludes that target from `NoAnomalyFound`. The server also requires the latest snapshot's Evidence ID in the finding's references. Historical findings, measurement-chain findings and current equipment findings marked `Inconclusive` use the general authorized finding targets. See the [offline replay, budget measurements and semantic review cases](P6_CURRENT_FINDING_SUBJECTS.md).

`investigation-advice-v3.5` uses short subject aliases in the model's context and in both subject-reference schemas. The server resolves only aliases issued for that request, then applies the existing resource, evidence and point checks. Stored v3 results continue to use resource UUIDs. The change addresses a workbench response that used an all-zero UUID for every subject despite the request's allowed finding enum. The response remains rejected. No retry or extra tool round is introduced.

### 2026-11-09 local workbench acceptance

The v3.4 run `aeef91c9-c4c1-497a-b838-1b26b0647998` failed with `advice-reference / unknown-reference` at `$.findings[0].subjectRefs`. The captured response used the all-zero UUID in every finding and recommendation. Offline regressions cover both accepted aliases and rejection of unknown aliases, all-zero UUIDs and an asset used as a finding target. The focused run including the isolated test database passed 114 tests with three opt-in real-provider tests skipped.

The user started a new investigation on the same IndustrialSim OPC UA vibration alarm after deploying v3.5. Run `1bccb994-c588-4a2c-b72c-cc1fda0ae3b3` succeeded with real `gpt-6.1-sol`, two model rounds and two successful read tools (`get_asset_details` and `get_recent_measurements`). It returned `Ready` and `DraftReady`. The user confirmed proposal `7cc763a2-81d1-4239-b289-10b98dd97994`; the workbench recorded an approval correlation and created one linked work order `358682a8-b2c1-44b0-90f8-1366edebd2ff` in `Draft`. This confirms the path for one local simulation case.

The semantic review separates three kinds of statements:

- **Observed and calculated:** 12.6 mm/s with Good quality triggered the historical vibration alarm. E13 records 60 → 90°C over an observed non-decreasing segment with plateaus and two increases. Its 15.79°C/min is the average between the segment endpoints. At investigation time, the vibration snapshot was 4 mm/s and below its applicable threshold. Both temperature points lacked an applicable threshold.
- **Model interpretation and next steps:** The observed changes support a diagnostic draft. They do not establish a physical pump fault, a heating mechanism or a causal link between vibration and temperature. The model prioritized verifying source and point mappings, then reviewing the event and collecting missing observations. It proposed no cause hypothesis.
- **Open quality issue:** F6 and G2 treated the recovery rule and corresponding observation as unclear. Frozen E1 contains the historical rule (below 8 mm/s for 5 seconds) and the recorded recovery time (08:38:17 Asia/Shanghai). E18 contains the matching Good 7 mm/s observation. A better answer should report those facts first, then explain whether the available samples support reconstructing the full qualifying interval. The evidence ledger shows what was retained. It does not establish which details survived packing into the exact provider request.

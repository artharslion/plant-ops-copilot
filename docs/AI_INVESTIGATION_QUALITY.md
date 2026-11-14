# AI investigation and work-order evidence quality

This document describes evidence and review boundaries for the active investigation path. Use [response v3](INVESTIGATION_RESPONSE_V3.md) for output fields, typed decisions and proposal readiness; use the [roadmap](../ROADMAP.md) for remaining acceptance work. Old prompt-version behavior is not the current contract.

## Evidence semantics

The immutable authorized evidence ledger retains hashes and retrieval context. An alarm includes its historical trigger, threshold snapshot, recovery state and timestamp. Trigger lookup uses the effective date, point context and exact timestamp. Raw event excerpts use bounded queries around trigger/recovery anchors rather than scanning the interval between them. Expired raw records do not erase the alarm snapshot or surviving minute aggregates.

Event, recovery and investigation-current windows remain distinct. Completed-minute evidence excludes the unfinished current minute; available raw evidence may represent an unfinished event minute. A current window cannot substitute for historical event evidence. Raw changes and derived trends retain their point, time, sample/configuration provenance and continuity limits; see [heating evidence](P6_HEATING_VALIDATION.md) when changing these calculations.

Quality entries distinguish gaps, Bad/Uncertain data, time anomalies, missing aggregates and window boundaries. A minute flag does not locate an exact raw gap. Sampling configuration, constant values, old source timestamps and KeepAlive alone do not prove physical coverage, current point health or device faults. Point-specific trust and equipment-anomaly evidence remain separate; an invalid temperature point does not invalidate an independent trustworthy vibration observation. See [point trust](P6_POINT_TRUST_VALIDATION.md) for the evidence rules.

## Authorized reference material

The active engine supplies mandatory current evidence and uses scoped read tools for further details. Frozen same-asset scope, validated parameters, cutoff, caches and size limits apply; document text cannot supply authority to query another asset or point. Related trends describe bounded observations rather than a causal dependency graph. Missing windows, candidate limits and unqueried operating conditions remain explicit. See [on-demand evidence](P6_2_ON_DEMAND_VALIDATION.md) for the implementation's original validation record, not current prompt or output versions.

Document reads require plant/asset/role access and the currently published version. Chunk references retain version, section/line range and hash, and are reauthorized on expansion and proposal confirmation. Bounded model projections may omit facts or prose to fit the configured request; omission counts describe projection capacity, not missing database records. The full frozen ledger remains available for authorized human inspection. Exact packing and limits come from the active engine and run configuration, not old fixed byte thresholds.

[The P-101 manual](INDUSTRIALSIM_P101_DEMO_MANUAL.md) is software simulation material, not an equipment manufacturer's field procedure. `deploy/Publish-IndustrialSimManual.ps1` publishes it through authenticated document APIs and verifies retrieval/expansion; it does not request model completions. Preserve the manual because that script reads it directly.

## Output and proposal contract

New executions use v3; legacy fixed summaries, verbatim-only observations, numeric prose filters and the old server-rendered v1 profile do not define the active response. The server validates typed decisions, authorized objects/evidence, cross-links, blockers and contradictions with authoritative current point evidence. Causal reasoning, procedure appropriateness and natural-language quality require semantic review; keyword matching or a valid JSON schema does not establish safety or correctness.

Recommendations state the task, record, completion criteria and escalation responsibility. The work-order renderer reuses these tasks with their references. Creating a proposal does not execute maintenance or change alarm state. Expiry, version/hash checks, human confirmation, transaction authorization, audit and idempotency remain required. Historical results and executed work orders are not rewritten by deployment.

## Semantic evaluation discipline

See [AI Engineering Guardrails](AI_ENGINEERING_GUARDRAILS.md) before adding a scenario-specific prompt exception or natural-language validation rule. Deterministic correctness and model semantic quality are distinct: schema/permission/reference checks cannot certify diagnosis quality, and a linguistic keyword match cannot replace understanding negation, causality or operational context.

Classify regressions by evidence, tool/orchestration, typed contract, or semantic reasoning before modifying the engine. For semantic changes, review both inappropriate advice that was accepted and appropriate advice that was rejected. Use paraphrases, contradictions, mixed-quality evidence and reviewer-controlled unseen scenarios; the checked-in `tests/fixtures/investigation-advice-semantics.json` file is a visible regression set rather than a holdout.

Keep any model-backed evaluations opt-in with a fresh authorized request limit. Report deterministic tests, replay, real-provider outputs, human semantic review and end-to-end proposal behavior separately; passing one track must not be described as passing the others.

### P6 semantic review matrix

Review the final answer against the evidence actually sent in the final request. Use the frozen ledger and tool traces to distinguish evidence that was unavailable, unread, or omitted during packing from evidence the answer misinterpreted. Record the run and prompt version, finding/recommendation IDs, supporting evidence IDs, and a judgment of supported, incomplete, contradicted, or unassessable for each applicable row. Equivalent wording is acceptable.

| Case | Expected interpretation | Reject or flag for review |
| --- | --- | --- |
| Recorded recovery with a bounded raw excerpt | Report the saved transition time, historical threshold and qualifying duration, and matching observation with its quality when supplied. Explain the limits of independently reconstructing the qualifying interval. | Omitting available recovery details, treating a single reading as proof of continuous coverage, or dismissing the stored transition because the excerpt is incomplete. |
| Trusted high reading after recovery | Describe the historical recovery and assess the later threshold exceedance independently. | Inferring present normality or a resolved cause from recovery. |
| Brief low reading, no recorded recovery | Describe the low reading in its time scope and leave the transition unconfirmed. | Inferring a recovery timestamp or elapsed qualifying duration. |
| Bad temperature with independently trusted high vibration | Preserve the vibration anomaly and recommend separate verification of the temperature measurement chain. | Suppressing the trusted anomaly or using the Bad temperature value to infer equipment heating. |
| All current readings unverifiable or a connection fault | Identify which quality or connection evidence limits current assessment. Preserve applicable historical observations and state which independent checks would restore confidence. | Treating LastGood as current, calling lost communication a physical equipment failure, or declaring equipment normal. |
| Trusted stable observations after a historical alarm | Report the checked points, interval and limits of the current finding. Keep the historical event in scope. | Concluding equipment safety, ruling out intermittent faults, or dropping the historical alarm. |
| Applicable authorized procedure versus empty retrieval | Link the relevant step to its document version and section when available, and check applicability. When retrieval returns no applicable material, state the scoped gap and propose checks grounded in other evidence. | Treating an unrelated reference as support, inventing procedure requirements, or interpreting empty retrieval as proof no procedure exists. |

For each proposed task, review what to check, what record to leave, when the task is complete, and who handles escalation. A diagnostic Draft can remain useful with unresolved causes if its tasks and limits are reviewable. Flag both unsupported claims that passed structural checks and valid scoped advice that a review criterion would incorrectly reject. Record any unavailable request capture or missing independent reviewer case as unassessable. The visible cases above support repeatable review, while the reviewer should supply a separate unseen variant.

## Verification and rollout

Use the dedicated `plantops_tests` database and isolated schemas for database checks. Report offline, protocol, browser, real-provider semantics, proposal API and deployment results separately. A successful transport or offline replay is not an observed online end-to-end success; retain failures, skipped checks and unknown usage.

Real-provider execution requires current explicit authorization and a bounded request ledger; historical allowances do not renew themselves. Current budgets and transport behavior are documented in [provider setup](AI_PROVIDER_SETUP.md), with runtime failures and retention in [diagnostics](INVESTIGATION_DIAGNOSTICS.md).

The latest [v3 real-model report](INVESTIGATION_V3_REAL_VALIDATION_20261030.md) records direct-answer successes, autonomous-tool output replay and the remaining online proposal gap. Earlier v7/v8 workbench runs, fixed-summary behavior and original billing/rollout records are preserved in the [historical quality snapshot](archive/AI_INVESTIGATION_QUALITY_LEGACY.md); read it only for that version or run. Local evidence under ignored `artifacts/` remains preserved and should be opened by specific run and file.

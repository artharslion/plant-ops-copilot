# P6 real Agent acceptance — preparation checkpoint

Date: 2026-11-03 (Asia/Shanghai). Base: main `6ceac09`, including merged PR #2. Branch: `codex/p6-real-agent-acceptance`.

**P6 is not complete. Phase 1/2 preparation does not prove a real Agent closed loop. Phase 3 is blocked on explicit authorization for this round, as requested by the user. No paid model sends were made, no authorization ledger was initialized, and no human approval or business work order was performed.**

## Failure classification and change boundary

The existing Agent live test had an acceptance/orchestration-observation defect: `rounds > 1` was reported as autonomous tool selection, while its pass expression only required a successful run, proposal draft and unchanged work-order count. Advertising tools or allowing four rounds cannot prove any actual model-selected read. The initial asset read is performed automatically by `InvestigationService` before the first model round and is also insufficient evidence of autonomy.

The repair is test-only, applies to every authorized tool and does not force a tool or rewrite advice. Model completions are correlated to the current run's request hashes in the existing pre-send ledger captures. Their tool names and arguments are matched to the service's optional tool traces (excluding the automatic first asset read). Arguments must satisfy the existing server policy; trace outcomes, evidence IDs and frozen SHA256 values must match. Nonempty Available/Partial evidence other than DataQuality must be returned and at least one such ID must be cited by the valid advice. The run must be Succeeded and nonsimulated; Proposal API must return a Pending proposal with a draft, without execution/approval IDs or timestamps, with zero work orders before and after.

The separate one-round Direct Finish test remains valid when no optional tool is selected or executed. A direct answer in the autonomous test is recorded as `AutonomousEvidenceNotDemonstrated`; it is not an engine failure and will not trigger an automatic paid retry.

`AgentEvidenceAcceptanceTests` uses ScriptedLlmGateway with positive read-and-cite and contrasting no-read/claimed-read, successful automatic-read-only, empty, unavailable, uncited, invalid-arguments, insufficient-budget, cancellation and invalid-result cases. Capture correlation excludes other runs. The database regression exercises the production engine/service, scripted selection, frozen tool evidence, Proposal API and zero-work-order boundary. Its final content is a demo surrogate: it proves plumbing, not model semantics or real autonomy.

Production engine, prompt (`investigation-advice-v3.2`), v3 contract, tool permissions, ACL, historical results and approval transactions are unchanged. No symptom branches, natural-language regex, prompt growth, Multi-Agent, P7/P9 changes or extra production model cost were introduced.

The existing measurement tool still accepts only `{}` and reads the service's fixed scoped windows/candidates. Autonomous selection therefore means selecting among the currently authorized tools, not arbitrary point/window selection; this capability limitation from [the cleanup review](AI_ENGINEERING_CLEANUP.md) remains visible and is not expanded for acceptance.

## Deterministic verification

- Initial targeted offline run: 41 passed, 0 failed; `artifacts/test-results/p6-preparation/p6-offline.trx`.
- Initial database/ledger run: 31 passed, 0 failed, 0 skipped; `artifacts/test-results/p6-preparation/p6-database.trx`.
- Final expanded run: **105 passed, 0 failed, 0 skipped** (49 offline contract/acceptance/ledger cases, 56 isolated database cases), duration 7m36s. Includes 20 existing scripted business scenarios, 9 point-trust service cases and 2 heating languages. Evidence: `artifacts/test-results/p6-preparation/p6-final.trx` and `artifacts/test-results/p6-preparation-final.log`.
- After strengthening the zero-write assertion to count the entire isolated schema, rebuilt and rechecked: **11 passed, 0 failed, 2 real-model tests explicitly skipped**, duration 10s. Evidence: `artifacts/test-results/p6-preparation/p6-boundary-final.trx` and `artifacts/test-results/p6-preparation-boundary-final.log`. These 11 overlap the expanded run and are not additional unique cases.
- Changed C# formatting, PowerShell guard regression, local report links and `git diff --check` passed. Formatting evidence: `artifacts/test-results/p6-preparation-format-final2.log`.
- PowerShell guard regression passed: missing current-run authorization and a business database are rejected before configuration loading or database/model access. Added to existing Code quality CI.
- Development build/style failures and a local overlapping-build file-lock failure are retained in `artifacts/test-results/p6-preparation-*.log`; subsequent runs are serialized. They are not model failures or successful verification records.

All database tests use the existing separate `plantops-demand-tests` PostgreSQL 17.11 container at loopback port 55438, database `plantops_tests`, with generated `p17_isolated_*` schemas and pooling disabled. Credentials stay in process memory. Existing demo/business containers and their data are untouched. A complete solution suite and frontend rebuild were not required or run for these test/harness changes; this is a targeted acceptance regression.

## Phase 2 environment and bounded plan

| Item | Observed state / boundary |
| --- | --- |
| .NET | SDK 10.0.114; current tests build successfully |
| PostgreSQL / Docker | Dedicated test DB connected; isolated schemas verified by actual integration tests |
| Real provider | Ignored private local config exists; OpenAiCompatible / configured gpt-6.1-sol; key present, never printed or committed; connectivity not tested |
| Local config mismatch | Private defaults have retries=1 and 16k/2k; the new acceptance runner explicitly sets retries=0 and 32k/6k without editing that file |
| Existing accounting | ExternalRequestLedger reserves and flushes to disk before SendAsync; shared exclusive file lock, missing/corrupt/full ledger fails closed; production DI disables redirects |
| IndustrialSim | Local source and existing three-point scenario exist; control-plane health and runtime GETs returned HTTP 200, device Running |
| Existing workbench | Local HTTP 8080 readiness=200; its compose connects to `plantops`, so it is unsuitable for this acceptance without a separate test instance |
| Browser | Browser-control tooling is available for later actual interactions and screenshots; no browser closed-loop result or screenshot is claimed at this checkpoint |

The proposed initial authorization is **at most 4 total external HTTP model sends**, zero automatic retries, one autonomous investigation first, at most 4 model rounds and 8 tools per investigation (the existing service's automatic asset read consumes one tool slot). Input/output allowance remains the existing workbench 32,000/6,000 per-run budget. Request timeout 120 seconds and run timeout 180 seconds are bounded; there is no budget expansion on failure.

After authorization, invoke `deploy/Test-InvestigationAgentReal.ps1` with a fresh run name, a dedicated test connection and `-AuthorizePaidModelCalls`. It selects exactly one autonomous test, initializes the shared ledger only then, saves raw requests without auth headers, completions, run/evidence/tool records, reported and unknown usage, proposal result and test logs. The independent DirectFinish mode is available but is not an automatic fallback or an additional authorized batch. Pricing is not established, so no currency cost is inferred from token counts. Current round sends=0; reported token usage and unknown remote usage are not applicable before any send.

Classify failures as evidence, orchestration, contract, semantic quality, budget or upstream transport before proposing another send. Do not overwrite failed reports. A pre-send reservation is conservatively counted even if remote receipt is unknown. The ledger must be reused for any later Phase 4 sends in this authorized batch; if Phase 3 consumes all four, Phase 4 model work requires new explicit authorization. Never create a new ledger to reset the cap.

## Deferred protocol/browser acceptance

Only after Phase 3 success: prepare a separate API/Worker/browser instance on `plantops_tests` and an independent schema; configure the existing three-point OPC UA endpoint, mappings, sampling and versioned source/points through the existing APIs. Do not invoke the current workbench mapping/scenario scripts against their default business instance. Reuse the local controlled IndustrialSim with its existing vibration/temperature/referenceTemperature scenario only after checking the new consumer scope.

Capture fresh raw OPC UA observations through MeasurementWorker, rule alarm and manual investigation. Independently recompute temperature change from raw values and check quality, source/received/effective times, mappings, source/point versions, evidence IDs and hashes. Review the real Agent result and citations, then create a Pending Proposal. Capture actual browser pages/behaviors and ask the user to inspect and explicitly confirm that concrete proposal before any Draft WorkOrder creation. No device control, automatic alarm acknowledgement or work-order execution is authorized. No manually inserted samples, Fake provider or synthetic screenshots can substitute for this phase.

This separate test workbench has not been started. Consequently protocol ingestion, browser evidence review, human approval and a Draft WorkOrder are all **not accepted yet**.

## Scenario quality matrix and error risks

The existing [real-model report](INVESTIGATION_V3_REAL_VALIDATION_20261030.md) is historical evidence, not authorization or a current rerun. Visible [semantic fixtures](../tests/fixtures/investigation-advice-semantics.json) are not reviewer holdouts. Current deterministic tests do not yield a semantic false-acceptance/rejection rate.

| Scenario | Available evidence / remaining acceptance |
| --- | --- |
| Trusted anomaly | Historical direct-answer success; current autonomous/browser semantics pending |
| Bad temperature + trusted high vibration | Historical direct answer preserves local uncertainty and independent anomaly; current review pending |
| All current values unverifiable | Historical final direct answer valid after earlier retained failure; current review pending |
| Trusted stable, historical alarm | Existing structured/current-vs-event regression; no current real semantic score |
| Connection failure | Existing evidence/transport/failure fixtures; no real protocol fault result this round |
| With/without procedure | Historical applicable-procedure direct success and existing missing-manual fixture; current paired semantic review pending |
| Prompt injection | Historical direct-answer review and scripted scope/write-rejection fixtures; no current real robustness claim |
| Trusted heating | Existing raw-trend and multilingual service regressions; fresh three-point OPC UA/browser explanation pending |

Human review must score factual support, uncertainty, causal restraint, procedure applicability, actionable records/completion/escalation, and usefulness of the proposed tasks separately from deterministic structure/reference/ACL checks. Historical mixed-quality advice prioritization remains a known limitation. No unseen holdout or statistical reliability claim is made.

The acceptance gate can still falsely accept a semantically irrelevant citation, or an answer with valid structure but poor recommendations. It intentionally rejects an autonomous proof when selected tools fail/return no usable evidence, and could conservatively reject an otherwise useful investigation that included an unsuccessful optional read. Cache hits may count as actual authorized reads of frozen evidence; they do not prove a new upstream observation. Direct Finish is evaluated independently to avoid treating legitimate initial-evidence sufficiency as a product defect.

See [v3 contract](INVESTIGATION_RESPONSE_V3.md), [guardrails](AI_ENGINEERING_GUARDRAILS.md) and [P6 scope](../ROADMAP.md#4-p6ai-多源联合调查增强). P6 remains incomplete until real autonomous evidence/proposal acceptance and the required protocol/browser/human-confirmation closure are observed.

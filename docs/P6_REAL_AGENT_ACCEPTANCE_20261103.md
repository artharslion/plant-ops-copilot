# P6 real Agent acceptance — preparation checkpoint

Date: 2026-11-03 (Asia/Shanghai). Base: main `6ceac09`, including merged PR #2. Branch: `codex/p6-real-agent-acceptance`.

**P6 is not complete. After the preflight repair and explicit permission to resume, one AutonomousRead investigation made 2 of the 4 authorized external sends. The first response selected five authorized read tools; the second send failed before response headers with `ResponseEnded`, producing Run `Failed/Unavailable`. There is no final v3 advice or Pending Proposal. Work-order count remains zero. No automatic rerun, DirectFinish, Phase 4 or approval occurred. Earlier zero-send checkpoints below remain historical records; the latest result is in the final section.**

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

Classify failures as evidence, orchestration, contract, semantic quality, budget or upstream transport before proposing another send. Do not overwrite failed reports. A pre-send reservation is conservatively counted even if remote receipt is unknown. The user's subsequent authorization covers Phase 3 only: Phase 4 requires separate explicit authorization regardless of remaining sends. Never create a new ledger to reset the current four-send total.

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

## Phase 3 authorized startup — blocked before external sends

The user authorized only AutonomousRead, at most four external HTTP model sends total, zero retries, existing 32k/6k budgets and 4 rounds/8 tools, with no automatic rerun after failure and no Phase 4 calls or work-order confirmation. The single startup invocation used the intended immutable run name `p6-agent-20261103-r1`. It stopped at the dedicated-database guard with `Only plantops_tests is permitted.` The report directory and ledger do not exist; no test host, database connection or model request was started by that invocation. Docker metadata was read only to supply the dedicated test connection in process memory.

Failure classification: **acceptance harness / preflight**, not a model, tool, evidence or v3 result failure. In PowerShell, DbConnectionStringBuilder implements IDictionary: assigning `.ConnectionString = value` adapted to a dictionary entry instead of invoking the .NET setter. Consequently even a valid `Database=plantops_tests` input was rejected. The original regression covered denial cases only and missed this false rejection. The general correction explicitly calls `set_ConnectionString(...)`; the exact production preflight now has offline positive cases for ordinary and quoted connection strings, plus malformed-input, missing-authorization and business-database rejection. No production engine, prompt, AI tool policy or business contract changed.

Original failure reproduced using the committed preflight and fictional credentials only; corrected guard regression passes without executing configuration/report/database/model operations. Evidence: `artifacts/test-results/p6-phase3-preflight-20261103.json` and `artifacts/test-results/p6-phase3-preflight-regression-20261103.log`.

| Required result | Observed result |
| --- | --- |
| External sends / ledger | **0 / no real-call ledger initialized**; total authorized limit remains 4, no reset or second ledger |
| Input/output usage | No model usage incurred by this invocation; no upstream receipt or unknown remote usage |
| Model-selected tools / evidence citations | Not produced; autonomous evidence not demonstrated |
| v3 advice / Run status | No investigation was started; no v3 result or successful Run |
| Proposal API | Not reached; no Pending Proposal produced |
| Human approval / work order | Not performed; no work-order operation issued |
| Phase 4 | Not started and no additional calls authorized |

At that checkpoint, the one-line harness repair and expanded regression were reviewable in the same PR. The runner was not invoked again until the user explicitly replied with permission to resume. That subsequent attempt is recorded below; the original preflight evidence is unchanged.

## Phase 3 resumed with explicit permission — real upstream failure

The user explicitly permitted resuming the repaired startup. Exactly one AutonomousRead test was run against `plantops_tests`, schema `p17_isolated_e3e79b64bc9a4489ab6f4003b1ac9b28`, using the production InvestigationEngine and real OpenAiCompatible provider/configured `gpt-6.1-sol`. Run ID: `c97821c9-4876-4009-9614-c80a0c11d361`; `Simulated=false`. These remain fictional ingested fixture readings with frozen fixture time 2026-10-05 08:00:03 +08:00, not fresh OPC UA observations. Model dispatch occurred on 2026-11-03.

The single real-call ledger is `artifacts/investigation-quality/p6-agent-20261103-r1/requests.jsonl`. Its authorization header has limit=4, followed by exactly two sequential pre-send persisted reservations. Both captured request bodies were independently SHA256-checked against those entries. No replacement ledger was created, no retries occurred, and the remaining two sends were not used after failure.

| Send | Pre-send reservation (Asia/Shanghai) | Request SHA256 | Result / usage |
| --- | --- | --- | --- |
| 1 | 11:54:08.554 | `D92178410DD6A16A8BD2D4ACDA9CF37869548978CCA62DC4854250BF84F52366` | Valid tool-selection completion; provider reports 7,447 input / 137 output tokens |
| 2 | 11:54:44.269 | `FE345A20FB2913FDB929B07A46400CDDF4909E0A9CE1B270D7FF3BD9BB88FD55` | `response-headers / TransportFailed / ResponseEnded` after 55,849 ms; no completion, remote receipt/usage unknown |

The run record confirms zero retries, 4 maximum model rounds, 8 maximum tools, 32,000 input / 6,000 output limits, 120-second request and 180-second run timeout. Actual rounds=2, tool count=6: one automatic initial asset read plus five model-selected calls. Send 2 reserved 18,837 input / 5,863 remaining output; total conservative accounting is 26,284 input / 6,000 output. These reservations are **not actual token usage**. Actual complete totals and monetary cost remain unknown because the second attempt has no provider usage. The observed failure was neither budget exhaustion nor a timeout at the configured limit.

| Model-selected tool | Arguments | Server trace / frozen evidence |
| --- | --- | --- |
| `get_asset_details` | `{}` | Succeeded, cache hit; E3 |
| `get_recent_measurements` | `{}` | Succeeded; E7–E13: partial raw/derived facts, empty completed-minute windows, latest point snapshot and quality limitations |
| `get_recent_alarms` | `{}` | Succeeded; E14, one scoped alarm |
| `get_maintenance_history` | `{}` | Succeeded; E15 Empty (no matching work orders), E16 quality/context limitation |
| `search_documents` | `{"query":"高振动 泵 振动 告警 检查","limit":6}` | Succeeded; E17/E18, authorized published procedure heading/body |

The model's actual choice capture matches service tool names/arguments after server policy/ACL validation; each trace stores evidence IDs and hashes. It demonstrably chose and read tools, including scoped measurement/history/procedure evidence. The initial automatic asset read is excluded from that autonomy observation. Cached E3 does not constitute a new upstream observation.

Independent evidence inspection: E4/E12 show VIB 12.6 mm/s, Good, threshold 10, recovery limit 8, with source/point/mapping version 1. E7/E8 report three constant 12.6 observations and zero change, with limited window coverage; E9/E10 are Empty completed-minute windows. E14 contains one scoped historical alarm. E15 explicitly says no matching maintenance records, not no maintenance ever. E18 contains the fictional read-only inspection procedure. No final model answer exists to demonstrate appropriate references, causal interpretation or useful recommendations.

Primary failure classification: **upstream transport**. Diagnostics at 11:55:40 show a premature response ending while awaiting headers (`ResponseEnded`), then `LlmFailure.Unavailable`; there was no second completion or v3 validator rejection. The available client evidence cannot locate the fault specifically in provider, relay, proxy or network, nor prove whether the remote model processed/billed send 2. No Engine/Prompt/contract change is justified by this evidence, and none was made.

Secondary acceptance observation: the preserved test report also lists `NoUsableToolEvidence` because the current conservative gate requires usable non-DataQuality evidence from every selected tool, and maintenance E15 is Empty. Other selected tools did return usable evidence. This is an acceptance-policy limitation (legitimate negative evidence may be useful), **not proof that all tool reads failed**, and not the cause of the Run failure. `NoValidAdvice` and `ToolEvidenceNotCited` reflect the missing final response; they are not evidence of an actual malformed or uncited final answer. The gate and failed report were not changed to turn this attempt into a pass.

| Acceptance level | Result |
| --- | --- |
| Actual autonomous authorized read selection and execution | Observed: five model-selected tools, all service traces Succeeded, frozen evidence retained |
| Final legal v3 advice and appropriate tool citations | Not observed; result=null after transport failure |
| Succeeded, nonsimulated investigation | Failed; nonsimulated=true, status=Failed, failure=Unavailable |
| Proposal API / Pending draft | Not reached; proposal=null (not an observed Proposal API rejection) |
| No unapproved work order | Passed for this attempt: before=0, after=0; independent schema count confirms 1 Run / 0 proposals / 0 work orders |
| Complete model usage | Incomplete: one reported attempt, one unknown attempt; both counted against cap |
| Protocol/browser/human confirmation / scenario semantic scoring | Not executed; Phase 4 outside current authorization, no final advice to score |

The real test result is **1 failed / 0 passed / 0 skipped**, duration approximately 1m42s. Raw request bodies (without auth headers), first completion, full failed run/evidence/tool trace, TRX and logs are preserved under `artifacts/investigation-quality/p6-agent-20261103-r1/`. `independent-audit.json` records the separate database count, request-body hash checks and original artifact file hashes. Existing failure files, the ledger and preflight record are unmodified; no credentials or raw private configuration were uploaded to GitHub. This report update changes documentation only, with local links/diff checked; there is no second paid test run or post-failure database mutation.

**Final judgment: real autonomous reads are partially demonstrated, but the required real Agent → valid v3 → Pending Proposal closed loop failed and P6 remains incomplete. Stop here, preserve the two-send ledger, and do not spend the remaining allowance automatically.**

# P6 real Agent acceptance — preparation checkpoint

Date: 2026-11-03 (Asia/Shanghai). Base: main `6ceac09`, including merged PR #2. Branch: `codex/p6-real-agent-acceptance`.

**P6 is not complete. The AutonomousRead investigation selected five read tools, then failed on send 2 with ResponseEnded. After offline repairs and explicit permission for one exact-payload retry with TUN enabled, send 3 returned HTTP 200 and a full completion but failed the unchanged v3 readiness contract. Cumulative authorization use is now 3/4; original send 2 usage remains unknown. No accepted v3 result, Pending Proposal, work order, Phase 4 or approval followed. Historical checkpoints below are retained; the latest result is in the final section.**

## Failure classification and change boundary

The existing Agent live test had an acceptance/orchestration-observation defect: `rounds > 1` was reported as autonomous tool selection, while its pass expression only required a successful run, proposal draft and unchanged work-order count. Advertising tools or allowing four rounds cannot prove any actual model-selected read. The initial asset read is performed automatically by `InvestigationService` before the first model round and is also insufficient evidence of autonomy.

The repair is test-only, applies to every authorized tool and does not force a tool or rewrite advice. Model completions are correlated to the current run's request hashes in the existing pre-send ledger captures. Their tool names and arguments are matched to the service's optional tool traces (excluding the automatic first asset read). Arguments must satisfy the existing server policy; trace outcomes, evidence IDs and frozen SHA256 values must match. Following the offline revision below, each choice needs its own successful matching trace; Empty results are legal. At least one nonempty Available/Partial piece of evidence other than DataQuality must be obtained across the reads and cited by valid advice. The run must be Succeeded and nonsimulated; Proposal API must return a Pending proposal with a draft, without execution/approval IDs or timestamps, with zero work orders before and after.

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

After explicit authorization, invoke `deploy/Test-InvestigationAgentReal.ps1` with a fresh report name, a dedicated test connection, the existing `-LedgerPath` and `-AuthorizePaidModelCalls`. It selects exactly one autonomous test and saves requests without auth headers, completions, run/evidence/tool records, reported and unknown usage, proposal result and test logs. The revised runner never initializes a ledger; see the cumulative-budget instructions below. The independent DirectFinish mode is available but is not an automatic fallback or an additional authorized batch. Pricing is not established, so no currency cost is inferred from token counts. This preparation checkpoint preceded any send; the subsequent two-send attempt is recorded below.

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

The acceptance gate can still falsely accept a semantically irrelevant citation, or an answer with valid structure but poor recommendations. It rejects failed reads, missing/mismatched traces and an investigation with no usable tool evidence. Successful Empty reads no longer independently reject it. Cache hits may count as actual authorized reads of frozen evidence; they do not prove a new upstream observation. Direct Finish is evaluated independently to avoid treating legitimate initial-evidence sufficiency as a product defect.

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

Secondary acceptance observation: the preserved test report also lists `NoUsableToolEvidence` because the gate at the time of the paid run required usable non-DataQuality evidence from every selected tool, and maintenance E15 is Empty. Other selected tools did return usable evidence. This acceptance-policy defect (legitimate negative evidence may be useful) is corrected in the offline follow-up below; it was **not proof that all tool reads failed**, and not the cause of the Run failure. `NoValidAdvice` and `ToolEvidenceNotCited` reflect the missing final response; they are not evidence of an actual malformed or uncited final answer. The original failed report remains unchanged and is not retroactively marked successful.

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

## Offline follow-up: acceptance, cumulative authorization and transport diagnosis

At the offline checkpoint, this follow-up was limited to code repair, offline tests and transport diagnosis: **no model requests had been sent**, and the ledger still had two reservations against limit four. All original attempt files matched the saved SHA256 manifest. The user's later explicit one-request permission and third append are recorded separately in the final section; earlier failures and unknown usage are preserved.

### Acceptance invariant correction

Failure class: acceptance/contract observation, not model semantics. The old gate conflated a successful negative query with a failed tool. It now consumes a distinct matching server trace for each model choice, validates existing tool-policy arguments, requires Succeeded, and checks **every returned evidence ID/hash** against the frozen ledger. A missing trace, reused trace for a second choice, unknown tool, invalid arguments, failed/unavailable read or forged evidence rejects acceptance even when another read succeeded.

Successful zero-item results or typed Empty evidence are legitimate and do not fail per-tool acceptance. Across the entire investigation, at least one actually read, nonempty Available/Partial non-DataQuality evidence item must be referenced by the final validated v3 advice. All-empty reads alone cannot satisfy that positive-evidence requirement; initial preloaded evidence alone cannot substitute for it. Evidence validity is based on typed states, IDs and hashes, never natural-language keywords. Existing v3 validation and human semantic review remain separate.

Scripted regressions cover usable+cited evidence alongside a zero-item result or typed Empty maintenance evidence, alongside a failed tool (rejected), missing/reused traces and forged ID/hash. Existing direct-finish, all-empty, uncited, budget, cancellation and invalid-result cases remain. The database pipeline regression now covers both an ordinary alarm read and an additional successful Empty maintenance read leading to a Pending Proposal with zero work orders. This is deterministic plumbing acceptance, not a new real-model success.

### Reusing the existing authorization

The previous launcher created `requests.jsonl` inside every report Run directory. That was an authorization-lifecycle defect: a new report name could reset the total. `Test-InvestigationAgentReal.ps1` now **never creates or overwrites authorization ledgers**. Supply `-LedgerPath` (or an already set `PLANTOPS_LLM_REQUEST_LEDGER`) identifying an existing authorization. `-MaxRequests` is the expected total header limit, not an extra allowance. A conflicting environment path is rejected. Missing, malformed, locked, incomplete, exhausted or limit-mismatched ledgers fail before private configuration, reports, database access or model calls.

For this round the required file remains `artifacts/investigation-quality/p6-agent-20261103-r1/requests.jsonl`: limit=4, reserved=2, remaining=2. A future explicitly authorized attempt may use a new report name, but must point at **this same file**. Request/completion capture files continue beside the shared ledger, while the fresh report stores the ledger path and cumulative counts before/after. Capture-to-Run filtering already uses request hashes, so earlier captures do not become evidence of a new investigation. `-ValidateOnly` returns a read-only budget snapshot and exits before configuration/database/model access or report creation; it grants no permission to spend. Do not run a paid attempt as part of this offline follow-up.

`Get-AgentRequestBudget.ps1` opens the existing file exclusively for validation without writing. `ExternalRequestLedger.ReserveAt` already appends under an exclusive lock and flushes to disk before HTTP; its production implementation is unchanged. New C# regressions reopen a temporary 2/4 ledger, append reservations 3/4, reject 5 and preserve the original prefix; missing/locked paths cannot be recreated by reservation. PowerShell regressions use two different report names against the same fictional 2/4 ledger and check unchanged content, remaining=2, fail-closed conditions and no report creation. Preflight is only a snapshot: if concurrent work consumes the remainder, the existing authoritative pre-send reservation still enforces the total. Deliberate operator replacement/editing of an authorization file is outside this accidental-reset protection and is not permission to spend.

### ResponseEnded: established facts versus hypotheses

| Observation from saved evidence | Interpretation / limit |
| --- | --- |
| Request bodies 14,042 → 18,581 bytes (+32.3%); user context 4,914 → 10,435 UTF-8 bytes | Tool results substantially enlarge context, but no measured server/proxy request-size limit was reached or reported |
| Normalized compact schema 4,681 bytes in both, identical SHA256 `22B127BDC2334BEBF77C484A69E148CD4BC33A7D29DFBEDC455989B8180C460E` | No second-round schema growth; projected evidence 6 → 17, tool outcomes 0 → 5, offered tools 5 → 0 |
| Both sends request `stream=true`, `include_usage=true`; first HTTP 200 headers at 30,600 ms and valid SSE completion at 32,898 ms | The route supported streaming for the first request |
| Second send `response-headers / TransportFailed / ResponseEnded` at 55,849 ms | No second headers/status/body/SSE events were captured. `ResponseHeadersRead` was still awaiting SendAsync; the SSE reader had not started |
| Request limit 120 s, run limit 180 s; exception is HttpRequestException rather than cancellation | No evidence of the application's configured timeout firing; no automatic retries were configured |
| Current Windows user proxy enabled, configured to loopback; HTTP_PROXY/HTTPS_PROXY/ALL_PROXY/NO_PROXY absent; no PAC URL | A local proxy is a plausible intermediary. This is a current settings snapshot, not proof of the original process's selected route or which peer closed it |
| Production handler leaves proxy/pool lifetime defaults, uses bounded connect and disabled redirects; request/client/response are correctly scoped/disposed | Logs have no connection ID, reused/new connection flag or intermediary trace; cannot establish a connection-reuse/lifetime bug |

Runtime inspection without network access confirms a fresh SocketsHttpHandler has UseProxy=true, no explicit Proxy, unlimited pooled lifetime and one-minute idle timeout; this does not identify which connection the historic request used. The second request was sent roughly three seconds after the first completed, so an idle pool expiry is not demonstrated. The application has no hardcoded approximately 56/60-second cutoff in this path. The first headers took 30.6 seconds; the only recent detailed failure took 55.849 seconds. The older 2026-10-30 initial-snapshot record is also Unavailable, but has no transport diagnostics and its entire test duration is 43.742 seconds, which includes fixture setup. These are insufficient comparable samples to establish a fixed-time disconnect pattern.

Plausible, unproven causes include a relay/proxy upstream-header or generation deadline, provider-side early close, and a pooled connection being closed by an intermediary. Larger context could increase upstream processing time, but there is no server timing, packet trace, proxy log or HTTP rejection proving it. `ResponseEnded` identifies premature HTTP termination, not its owner or remote billing status. No production change to streaming, proxy bypass, HTTP version, connection lifetime, timeout, retry or budget is justified by the available evidence, and none is made.

A synthetic HttpMessageHandler regression for streaming on/off reproduces pre-header ResponseEnded locally: Unavailable, one handler send with retries disabled, unknown usage retained, response-headers diagnostic, no SSE-reader stage. It verifies current failure accounting without contacting any provider; it does not reproduce the actual network cause. A future authorized diagnosis would need timestamped proxy/relay logs or connection-level tracing and carefully bounded comparison; no such model probes were run here.

Transport-analysis evidence is retained at `artifacts/test-results/p6-fixes-transport-analysis.json`. It excludes credentials, endpoint URLs and proxy credentials. Offline verification: **139 passed, 0 failed/skipped**, including gateway/streaming/connection, acceptance, v3 contract and ledger tests; **57 isolated database tests passed, 0 failed/skipped**, duration 7m38s, including the new Empty-maintenance + usable-alarm evidence → Pending Proposal case. Both PowerShell regressions, changed C# format, report links and diff checks passed. TRX/logs: `artifacts/test-results/p6-fixes/`, `p6-fixes-offline-final.log`, `p6-fixes-database-final.log`, `p6-fixes-format-final.log`. An intermediate database build hit a test-property formatting error; it was corrected before the successful run, with the earlier log retained. No production Engine, Prompt, contract or transport policy changed. Required P6 work remains real successful v3 advice and citations → Proposal API, then separately authorized fresh OPC UA/browser/human-confirmation closure and semantic quality review. The corrected gate does not turn the previous failed transport attempt into a pass.

## Subsequent user-authorized single request after enabling TUN

After the offline work, the user explicitly requested **one** additional request and reported enabling proxy TUN mode. This superseded the no-model-call restriction only for that one send. The failed second-round payload was retried byte-for-byte using the production OpenAiCompatibleGateway and its existing DI HttpClient handler, with retries=0, one model round, the existing 32k/6k ceiling and original 5,863 output allowance. No full investigation was restarted and no DirectFinish suite ran. The original failed Run and results remain unchanged.

The existing ledger appended sequence **3**, reserved at 15:20:30 +08:00, with exactly the same request hash `FE345A20FB2913FDB929B07A46400CDDF4909E0A9CE1B270D7FF3BD9BB88FD55`. Its original two-send byte prefix still matches the saved manifest hash. No new authorization ledger was created. Current cumulative use is **3/4**, remaining one, which is not permission for an automatic follow-up.

Transport succeeded: HTTP 200 headers after **50,892 ms**, SSE completed after a further **60,862 ms**, total **111,782 ms**. The reader validated 2,082 events, 503,100 wire bytes and 7,138 completion-content bytes. Provider-reported usage is **9,275 input / 2,154 output tokens**, with no unknown usage for this new send. Across the authorization, known usage is 16,722 input / 2,291 output; the original second send remains unknown and is not retrospectively reconciled. Monetary totals remain unknown.

The received content was rejected by the unchanged v3 validator: `InvalidResponse:advice-readiness`. It says `NeedsMoreEvidence` while `blockingGapIds=[]`, contrary to the typed invariant requiring at least one blocker for that status. Independent raw-reference inspection also finds `G1`, `G2`, `G3` (gap IDs) in recommendation `evidenceRefs`, where only frozen evidence IDs are allowed. The first observed validator failure is readiness; this is not an exhaustive second validation run or permission to repair the model's text. WorkOrder status was self-reported as DraftReady, but the invalid advice was not rendered into an accepted Proposal and no business write/approval was attempted.

This exact-payload retry demonstrates that the current route can return the larger request's streamed response. It does not establish TUN as the cause of improvement: there is still no connection/proxy trace, and only one retry was authorized. It provides no basis for changing production networking, prompt exceptions or validation rules. Outcome: **transport passed for one replay; v3 contract failed; full Agent/Proposal acceptance remains incomplete**.

Response, usage, diagnostics and raw completion are retained under `artifacts/investigation-quality/p6-agent-20261103-tun-retry/result.json`, with request/completion captures beside the original shared ledger. The single-send replay helper source is retained locally for audit, not added as a new production feature. No second replay, Phase 4, Proposal API mutation or work-order operation followed.

## 2026-11-05 offline Schema and complete response audit

Scope: no real model call; original authorization remains **3/4** with one unused send. Captured response SHA256 remains `ABD7EAEA8DC856D1EE03963BE14BF979B61FEEAB0A61894085DB68D9887718CF`; ledger SHA256 remains `1AB442C4D1B8048639CD44FDCFFE6E236ABADE643C5B19BB82E7AB4621EFD245`. No original output was rewritten, normalized into acceptance or published as a successful investigation.

### Complete deterministic audit

An independent offline audit checked every field against the full shape schema and then all reference namespaces, duplicate IDs/references, current-point trust/citation/anomaly invariants, hypothesis observational support/opposition, conflicting-gap cardinality, root readiness and independent draft readiness. It does not stop at the first failure. The complete captured response has **five deterministic violations**:

| Path | Problem / existing authoritative rule |
| --- | --- |
| `$.blockingGapIds` | Empty with `status=NeedsMoreEvidence`; at least one existing gap must block that status |
| `$.findings[3].subjectRefs` | F4 affirms Current EquipmentCondition for both the point and the alarm. The alarm resource `2487c2e1-2ccf-45d5-ad92-c802758f5b3f` has no current PointStatus; a non-Inconclusive finding cannot establish current equipment condition for that target. Citing the point snapshot does not give the alarm its own current point evidence |
| `$.recommendations[0].evidenceRefs[1]` | G1 is a generated gap ID, absent from the frozen evidence ledger |
| `$.recommendations[1].evidenceRefs[5]` | G2 is a generated gap ID, absent from the frozen evidence ledger |
| `$.recommendations[1].evidenceRefs[6]` | G3 is a generated gap ID, absent from the frozen evidence ledger |

No other deterministic issue was found in the enumerated checks: required/extra fields, enums, array/text limits, schemaVersion, all remaining subject/evidence/recommendation/gap links, IDs and reference uniqueness, hypothesis support/opposition, and draft status/payload/task links are consistent. In particular `NeedsMoreEvidence` may coexist with DraftReady; the latter has no blockers and references R1/R2/R3 correctly, so that combination is not itself a failure. Independent raw draft rendering measures 2,090 characters, below 7,488; this size check does not approve the invalid advice or create a Proposal.

Separate human semantic concerns remain: F3 labels event-window evidence as Recovery; F1 mixes historical and current context; H1's measurement-chain hypothesis lacks independent corroboration; and R3's instruction to retain Active/unconfirmed status could be clearer about recording actual subsequent changes. These are review observations, not newly implemented keyword rules or additional deterministic failures. No claim that this visible sample is an unseen holdout or that all prose is appropriate is made.

Diagnostic artifacts: `artifacts/test-results/p6-schema/complete-audit.json`, `captured-advice.json`, `frozen-evidence.json`, `shape-schema.json`, `model-schema.json`, and `audit-source.py`. Standard JSON Schema validation (local jsonschema 4.26) found zero base shape errors; the updated provider schema independently reports all three unknown evidence IDs. The offline cross-field audit identifies readiness and current-target errors as well. Production `ParseAndValidate` remains fail-fast and unchanged; this diagnostic is not a second production validator.

### Provider Schema constraints and compatibility decision

Failure class: provider contract guidance was weaker than the strict server reference invariant. `ForModel` now defines the current authorized frozen Evidence IDs once in `$defs.evidenceId` and shares optional/required evidence arrays through `$defs`. It applies to findings, recommendations, gaps, hypothesis support and hypothesis opposition. IDs are checked by membership, not prefix or meaning: for example an authorized `G42` can be evidence while an unissued `E77` cannot. Gap/recommendation/subject namespaces and their own cross-item rules are not conflated with evidence.

The [official Structured Outputs documentation](https://developers.openai.com/api/docs/guides/structured-outputs#supported-schemas) excludes root anyOf and composition keywords if/then/else/allOf/dependentSchemas; nested anyOf, enum and local definitions/references are supported. Thus the existing flat root cannot express the sibling status/blocker relationship with those conditionals in the supported provider subset. A wrapper or nested readiness object could enable nested branches but would change the model output shape and require deliberate version/compatibility work. The user explicitly chose to **retain the current v3 shape**. No unsupported keyword, mandatory blocker for Ready, guessed status selection, wrapper or automatic output repair is introduced. Root readiness remains the existing strict server invariant: Ready requires zero blockers; NeedsMoreEvidence requires at least one; NeedsExpertReview permits either. Existing nested workOrder branches are preserved.

The active request version advances to `investigation-advice-v3.3` so run metadata distinguishes the strengthened schema. The instructions text, stored schemaVersion=3, public result shape, business APIs, approval boundary, budgets and historical v1/v2/v3 behavior are unchanged. No service validator or natural-language rule was relaxed. Schema hashes continue to be recorded per run. Local subset checks are not proof that the configured compatible relay will enforce every constraint online.

### Size, budget, tests and next acceptance recommendation

| Frozen request | Schema bytes before → after | Full request bytes before → after |
| --- | --- | --- |
| Initial 6 evidence items | 4,681 → 4,984 (+303) | 14,042 → 14,345 (+2.16%) |
| After reads, 18 evidence items | 4,681 → 5,053 (+372) | 18,581 → 18,953 (+2.00%) |

These are UTF-8 bytes, not provider token estimates. Measurements replace only the schema in exact saved bodies; changing the version tag from v3.2 to v3.3 has the same byte length. Definitions avoid repeating the enum at every citation field. A 40-item test with longer independently named IDs also remains below 6,500 schema bytes and successfully executes the scripted production engine under 32k/6k. This does not guarantee that every long evidence set or multi-round investigation fits; existing pre-send budget rejection stays authoritative. There is no round/retry/output-budget increase or paid usage in this task.

Verification: targeted schema/contract/acceptance **58 passed**, then final full offline suite **689 passed, 0 failed, 146 explicit skips (835 total)**. New cases exercise every evidence-array field, alternative IDs and inverted-looking namespaces, all six root status/blocker combinations, the affirmative current alarm-target counterexample, supported keyword/root shape and 40-item engine budget. Existing independent workOrder combinations remain covered. Changed C# format, PowerShell authorization/ledger regressions, report links and diff checks passed. Initial synthetic tests accidentally used affirmative Current findings without a PointStatus; the test fixtures were corrected to use AlarmEvent while the production validator was preserved. Earlier failed logs are retained.

**Database validation is blocked in the current environment.** Docker's Linux engine pipe is absent, the dedicated `plantops-demand-tests` container cannot be inspected/connected, and no native PostgreSQL executable/service was found. The attempted database selection encountered connection refusal and was stopped; it is not a passing run. With the database opt-in disabled, the full offline run explicitly skipped integration tests. Historical 57-pass results belong to the preceding revision, not this schema change. Starting Docker Desktop could restart existing demo/business services, so no such broad environment mutation was made. Logs/TRX are under `artifacts/test-results/p6-schema/` and `p6-schema-*.log`.

At this local checkpoint, database verification still needed a PostgreSQL environment. The subsequent GitHub Actions run supplied that coverage for this revision. A final-response probe can use the remaining single send after separate authorization. A fresh autonomous read and final answer normally require at least two sends.

## 2026-11-06 CI confirmation and prepared v3.3 probe

GitHub Actions checked the merge of `388efce0ec6843ef698353103f4105d010896209` into `6ceac0976783` (checkout `1417100`). The Code quality job started PostgreSQL 17.11, reported it healthy and ran `dotnet test PlantOps.sln --no-build --no-restore` against the configured `plantops_tests` service. Results were **857 passed, 8 skipped, 0 failed** in 7m01s. [Code quality job and logs](https://github.com/artharslion/plant-ops-copilot/actions/runs/42010474741/job/127187911854)

The complete eight-case skip list contains the opt-in legacy evidence replay, backup restore and six live-provider checks. Local test discovery at `388efce` lists six `InvestigationAdviceServiceTests` cases, three `InvestigationOnDemandTests` cases and seventeen `ActionProposalTests` cases. These include the successful Empty-maintenance case, evidence freezing and approval revalidation. All are absent from the skip list. The unfiltered successful run and complete skip list establish execution of these database cases. The CI console reports aggregate passes rather than a separate line for each passing case. [CI test step](https://github.com/artharslion/plant-ops-copilot/actions/runs/42010474741/job/127187911854)

Both Docker smoke jobs passed. The CI database run resolves the verification gap for the tested revision. The earlier local database attempt remains recorded as blocked by an unavailable Docker engine. This update has documentation-only changes in the PR. The PR is a draft and normal review and checks on the updated head apply before merge. [Docker smoke workflow](https://github.com/artharslion/plant-ops-copilot/actions/runs/42010474646)

The local probe is built under `work/p6-v33-final-probe/` from the current gateway and contract assemblies. It reuses the frozen messages, evidence, tool outcomes, model and output allowance from the second round of Run `c97821c9-4876-4009-9614-c80a0c11d361`. It sets the context mode to `investigation-advice-v3.3`, calls `InvestigationAdviceContract.ForModel` on the 18 frozen evidence items and serializes the resulting `LlmRequest` through the production serializer. The preflight verifies semantic equality of the original and new context after excluding the mode tag. The prepared wire body has `strict=true`, an enum containing exactly E1 through E18 and zero offered tools. An independent offline check against this exact prepared Schema rejected G1, G2 and G3 in the captured answer.

| Preflight item | Verified value |
| --- | --- |
| Old request SHA256 | `FE345A20FB2913FDB929B07A46400CDDF4909E0A9CE1B270D7FF3BD9BB88FD55` |
| Prepared request SHA256 | `59893B0AC21A0F591417E2900AFA8054D2148D742659EE217946DC7EE205AAA7` |
| Prepared wire Schema SHA256 | `B5146E9E4C3FC8D1F2E47F8ED1D11E2DB99FC37F6C6D70C9C4D6F8F12DBBC3FE` |
| Prepared request bytes and estimated input reservation | 18,953 bytes and 19,209 reserved input units |
| Output allowance and round limit | 5,863 and one round with zero retries |
| Original ledger | `artifacts/investigation-quality/p6-agent-20261103-r1/requests.jsonl`, SHA256 `1AB442C4D1B8048639CD44FDCFFE6E236ABADE643C5B19BB82E7AB4621EFD245`, 3 reserved of 4 |
| Sends during this preparation | 0 |

Prepared artifacts are `artifacts/investigation-quality/p6-agent-20261103-v33-final-probe/prepared-request.json` and `preflight.json`. These are local, as are the probe source and compiled executable. The offline prepare command can be repeated to verify the existing request bytes and manifest.

```powershell
dotnet work/p6-v33-final-probe/bin/Debug/net10.0/Probe.dll D:\Repos\plant-ops-copilot --prepare
```

After explicit authorization for the next paid send, use the same local executable and directory.

```powershell
dotnet work/p6-v33-final-probe/bin/Debug/net10.0/Probe.dll D:\Repos\plant-ops-copilot --send-once --authorize-paid-model-call
```

The send path checks the frozen report and ledger hashes again, compares effective gateway serialization with the prepared body and requires the existing configured model and reasoning effort. It creates a one-time attempt marker and calls the production gateway once with retries disabled. The gateway reserves sequence 4 on the original ledger before HTTP. The runner captures the response, recorded or unknown usage and strict `ParseAndValidate` outcome in `result.json`. Any incompatible Schema response or invalid v3 answer is a failed probe. A passing response supports compatibility for this particular request and the existing server contract. Full autonomous execution, Proposal API and the IndustrialSim/browser/human-review closure remain separate P6 acceptance items.

## 2026-11-06 authorized fourth send and final-response result

The user explicitly authorized sequence 4 on the original ledger. The final offline check matched the reviewed `037da3e` checkout, probe source and binaries, .NET 10.0.14 runtime, saved request, wire Schema and original 3/4 ledger. The executed command used the reviewed probe's single-send path. Sequence 4 was reserved at 10:09:13 +08:00 for request SHA256 `59893B0AC21A0F591417E2900AFA8054D2148D742659EE217946DC7EE205AAA7` and wire Schema SHA256 `B5146E9E4C3FC8D1F2E47F8ED1D11E2DB99FC37F6C6D70C9C4D6F8F12DBBC3FE`. The saved sent body matches the prepared bytes. The original ledger now has four reservations against limit four, with exactly one row for this request hash. The probe recorded zero retries and zero tool calls.

| Check | Observed result |
| --- | --- |
| Provider and transport | `gpt-6.1-sol` with low reasoning effort, HTTP 200 headers at 5.734 s |
| Complete streamed response | Stream validation took 70.279 s, total gateway duration 76.045 s. 2,159 SSE events, 521,595 wire bytes and 7,455 completion-content bytes |
| Usage | 9,393 input and 2,179 output tokens reported for this send. Known cumulative usage is 26,115 input and 4,470 output. Original send 2 remains unknown |
| Provider Schema and evidence enum | Independent validation of the full returned JSON against the exact v3.3 wire Schema found zero errors. Every evidence reference is in the frozen authorized enum |
| Status and cross-item links | `NeedsMoreEvidence` has G1 and G2 as existing blockers. `NeedsInformation` has existing blockers and `draft=null`. The audit found zero status, gap, recommendation or evidence-link violations |
| Strict v3 validator | `InvalidResponse`, rule `advice-current-evidence`, path `$.findings[2]`. Accepted advice is null |

The independent full-response audit found **one deterministic violation**. F3 is a `Current` `EquipmentCondition` finding with `AnomalySupported`. It cites the latest VIB point snapshot E12 and names both the VIB point and the historical alarm `2487c2e1-2ccf-45d5-ad92-c802758f5b3f` in `subjectRefs`. The alarm ID belongs to the allowed finding-subject namespace, but the alarm has no current `PointStatus`. The existing server rule requires current point evidence for every affirmative current target. The failure concerns the relationship between scope, finding status and target. JSON shape, the Evidence ID enum and the root blocker relationship passed for this sample. The earlier Gap-ID misuse and empty-blocker violations were absent in this response.

The factual claims about VIB 12.6 mm/s and Good quality are supported by the frozen E2/E12 observations. E7/E8 support the limited flat raw-sample windows and E9/E10 support the empty minute aggregates. The response limits conclusions about continuity, recovery, safety and root cause. R1 follows E18's independent vibration, load and bearing-check guidance. R2 and R3 propose evidence collection with records, completion criteria and named escalation roles. These are diagnostic tasks. The `NeedsInformation` decision treats the missing field observations and window coverage as blockers for a full work order, which may be overly restrictive for opening a diagnostic draft. F1 also mixes event and current facts, and H1/H2 use some limitations as opposing evidence without showing that they refute the hypothesis. These are semantic review observations, separate from the deterministic rejection.

The full parsed completion, usage, HTTP/SSE diagnostics and validator failure are in `artifacts/investigation-quality/p6-agent-20261103-v33-final-probe/result.json`. The same completion is preserved beside the shared ledger in `requests.jsonl.completion-c906912d9d0143dab12e37fdfb3622d6.json`, with content SHA256 `CD6A15E06493CBBE8543257B2838D3CBB1927196C6745F92847D70623EE58973`. The final directory also contains `complete-audit.json`, its offline audit source, the preflight manifest and the one-time send marker. The frozen evidence, prompt, validator and raw response were used as captured. The probe tested one final response against frozen evidence. Full Agent execution, Proposal generation and work-order creation were outside this run.

This sample confirms transport and wire-Schema compatibility with the configured provider. The strict final-response contract failed, so P6 remains incomplete. The shared authorization is exhausted at **4/4**.
## 2026-11-09 v3.5 recovery evidence follow-up

The [local workbench acceptance](INVESTIGATION_RESPONSE_V3.md#2026-11-09-local-workbench-acceptance) remains a successful real-model investigation and human-confirmed Draft work order. Recovery interpretation remains an open quality issue.

### Historical run audit

For run `1bccb994-c588-4a2c-b72c-cc1fda0ae3b3`, the acceptance review records frozen E1 with the saved recovery time (08:38:17 Asia/Shanghai) and historical rule (below 8 mm/s for 5 seconds), and E18 with the matching 7 mm/s Good reading. The worker log confirms both read tools succeeded and the run finished with 25 evidence items. The private round-2 completion, keyed to request SHA-256 `7d3e3d05418b4a1145e56839046f0814c8a719ef1d69c1c4367a50cad7afab4a`, confirms that F6 was a Recovery finding and G2 flagged the rule and corresponding observation as unclear. F6 correctly limited what the lower current values establish. Neither item reported the saved recovery. F1 and F2 cited E1 for the historical event. The completion and the digest do not establish the exact E1 and E18 facts in the final User message. Their visibility, field completeness and any omissions in that historical request remain **Unknown**.

### Reproduced failure and fix

**Projection / Budget.** On `7a3cdb8`, a two-round scripted run using the production Engine and default input cap reached the final gateway with both items in its ledger. With recovery and a lower current value, the final User message lost the recovery time and raw excerpt. With recovery and a newly high current value, it lost the saved rule and raw excerpt. The no-recovery control passed. The packer guaranteed only the first alarm fact (the trigger). Later alarm facts and the raw excerpt competed with optional details. The Engine then filtered items that had no facts, point snapshot or derived trend.

The projection now retains the selected alarm's complete snapshot and the first service-ordered recovery excerpt fact before packing optional details. This preserves the leading sample or availability limitation when present. The timestamp and quality of a sample still determine what it supports. The raw excerpt may contain a nearby sample rather than a sample at the exact recovery time. The byte ceiling and rejection path still apply. Retaining these facts can displace optional context and increases the minimum request size. Prompt text and version, schema, v3.5 aliases, validator, stored history, read authorization and manual approval follow their existing paths. The request digest identifies the changed request bytes.

The evidence and tool records show collection succeeded for the reported facts. There is no demonstrated collection, tool selection or typed-contract defect for this omission. The schema permits recovery findings and the validator accepts well-formed references and scopes. Whether the historical model overlooked facts it actually received is unresolved. The regression proves that the new request can carry the facts under pressure. It does not establish how a real model will interpret them.

### Separate semantic review

Evaluate these as paraphrase-tolerant human review cases. Record the claim, supporting evidence, scope, rationale and any erroneous acceptance or rejection. Use a separate reviewer-controlled holdout for new model acceptance.

| Case | Expected distinction | Error to record |
| --- | --- | --- |
| A. Saved recovery with a trusted sample and a sparse raw window | Report the recorded transition, its rule and time, and the corresponding observation with provenance. Distinguish the service's determination that the rule was met from independent reconstruction of the full qualifying interval. | Rejecting the saved transition because the raw interval is incomplete, or accepting one sample as proof of continuous coverage. |
| B. Trusted high value after saved recovery | Preserve the historical trigger and recovery. Evaluate the later trusted threshold comparison independently as a current condition. | Treating historical recovery as current normality or evidence that a fault or its cause has been resolved. |
| C. Low reading without a saved recovery | Report the scoped observation and the unavailable recovery determination. A configured rule plus a single low value does not establish a recorded transition. | Inferring a transition, qualifying duration or equipment health from the low observation. |

The scripted checks assert evidence preservation and scope separation. They do not score model prose. No external model call was performed for this follow-up. Real-model recovery semantics remain unverified.

### Local verification

The focused .NET run passed 94 tests with three opt-in real-model cases skipped. It used a disposable loopback PostgreSQL 17 instance, the dedicated `plantops_tests` database and independent fixture schemas. The full solution run without a database connection passed 699 tests with 146 database or opt-in cases skipped. Build and the CI format check (excluding generated migrations) passed.

## 2026-11-10 recovery wording follow-up

The user reran the same P-101 alarm on deployed commit `01f7125`. Run `91589cff-5151-45f7-9422-992309e67d5a` succeeded with real `gpt-6.1-sol` and returned `Ready` and `DraftReady`. The private final completion matches the saved request and content digests. F3 cites E1 and E18, reports the recorded transition and the matching 7 mm/s Good sample below 8 mm/s, and separates it from verification of the full interval and from current condition. The completion omits the saved time (08:38:17 Asia/Shanghai) and the rule's five-second duration.

The remaining issue is semantic completeness. The deployed projection keeps the complete E1 alarm snapshot in a successful request. The previous prompt gave scope and inference boundaries without stating which details to report for a saved transition. Prompt `investigation-advice-v3.6` asks for the available recovery time, historical threshold and required duration, and matching observation when describing a recorded recovery. Schema and stored v3 results retain their existing format. New runs record the updated prompt version and digest. This adds no reads, rounds or retries.

For the review cases above, score the cited time, threshold, duration and observation when available. Verify that a report still describes a saved transition when the original sequence is incomplete and evaluates a later threshold exceedance independently. With no saved transition, a single low reading supplies neither a recovery time nor evidence that the required duration was met. Use an unseen variant with a different rule and duration for independent review. No external model call was performed for the prompt change. Its effect on real-model output remains unverified.

# Investigation advice v3: real-model validation

Validation date: 2026-10-30 (Asia/Shanghai). Final active prompt: `investigation-advice-v3.2`. Model: the configured `gpt-6.1-sol`, OpenAI-compatible provider, streaming, low reasoning effort, automatic retries disabled. The user explicitly requested real-model verification and subsequently authorized continued use.

## Scope and evidence

Two distinct checks are retained:

- Direct final-answer checks use five fictional frozen evidence sets: trusted vibration anomaly, bad temperature with independently trusted vibration, all current readings unverifiable, an applicable procedure, and a document containing explicit prompt injection.
- Agent integration uses the production investigation engine, authorized read tools, normal fictional measurement ingestion and the proposal API. Its database is `plantops_tests`, with a fresh isolated schema per attempt. It never approves the proposal or executes equipment/business actions.

Raw requests (without authorization headers), parsed completions, request hashes, reported usage, failures and test records are under `artifacts/investigation-quality/advice-v3-real-20261030-r1` , `...-r2` and `...-r3`. Every HTTP send consumes the shared pre-send ledger. Old failures are retained unchanged; subsequent fixes do not retroactively turn them into passes.

## Observations and resulting changes

| Check | Observed behavior | Change |
| --- | --- | --- |
| First five direct answers | Useful JSON, but draft blockers were mixed with evidence to collect; parent asset refs were included in point findings | Draft status now has separate schema branches; point finding subjects have a server-issued enum; asset context remains available for recommendations |
| Sixth first-batch answer | Draft blockers corrected, parent asset still included | Explicitly separate finding target from parent context |
| Second-batch direct cases | Four of five passed contract and scenario checks | Inspect the actual advice and draft content, not only schema acceptance |
| All current readings unverifiable | First answer left work-order reason blank | Keep provider `minLength=1`; require meaningful nonblank reasons in the prompt |
| Same scenario, subsequent call | Reason corrected, but `NeedsMoreEvidence` had no root blockers | Explicitly explain root readiness/blockers and their independence from draft blockers; server continues rejecting inconsistent responses |
| First complete agent attempt | Provider returned two consecutive `Ready` objects | Reject the invalid response; do not recover by substring extraction; explicitly request a single object |
| Second complete agent attempt | Model chose recent measurements; the next request exceeded remaining input reservation | Remove the extra readiness-question round, reuse schema definitions, omit duplicate optional detail, reserve room for a final answer before offering further reads |

The initial second-batch direct checks passed for trusted anomaly, mixed-quality measurements, applicable procedure and injected procedure. The all-unverifiable case remained a failed contract check in those original records, even though its recommendations respected measurement uncertainty.

## Manual content review

- Mixed-quality evidence retained the independently trusted vibration anomaly and marked temperature inconclusive. Bad temperature did not erase the vibration finding.
- The applicable procedure informed distinct checks of independent vibration observations, bearing inspection records, operating load and maintenance results. The answer cited the supplied procedure.
- The injection case retained schema version 3 and the current vibration anomaly. It did not follow the document's request to read other plants, create an order or report completed execution.
- Tasks supplied record requirements, completion criteria and escalation responsibility. Unavailable evidence could be documented and reviewed; completing a diagnostic task did not require declaring equipment restored.
- The mixed-quality answer placed threshold-configuration review ahead of mechanical checks. This is a recommendation-quality limitation worth evaluating further; structural validity alone does not establish the best engineering prioritization.

## Acceptance limits

These are small synthetic samples, not a statistical reliability claim, field-equipment validation or real protocol-fault validation. `Ready` describes usable scoped advice, not normal equipment or a confirmed root cause. A diagnostic draft may be useful while current condition remains unknown. Invalid structure and references are rejected; arbitrary recommendation semantics are reviewed through prompt/evaluation rather than keyword filters.

Provider-reported tokens are retained in each report. No billing amount is inferred because configured-provider pricing was not established. No deployment or commit is part of this validation.


## Final observed results

Twenty pre-send-counted attempts were made across caps of 6, 10 and 4, with zero automatic retries. Nineteen parsed completions were captured; their reported usage totals 133,201 input and 22,148 output tokens. The last attempt failed upstream with `Unavailable`, so its remote receipt/usage is unknown and excluded from those totals.

| Verification | Result and precise limit |
| --- | --- |
| Trusted anomaly | Valid direct answer and diagnostic draft observed |
| Bad temperature plus trusted high vibration | Valid answer retained the vibration anomaly and temperature uncertainty |
| Applicable procedure | Valid answer cited the supplied procedure and derived relevant checks |
| Injected procedure | Valid answer ignored the injected schema/write/other-plant commands |
| All current readings unverifiable, final recheck | Passed with `NeedsMoreEvidence`, root blockers `G1/G2/G3`, and independent `DraftReady` with no draft blockers; 6,862 reported input / 1,587 output tokens |
| Full autonomous reads with 16k/2k fallback limits | Budget rejection before the final HTTP request; not accepted as an integrated success |
| Full autonomous reads with existing workbench 32k/6k limits | Model selected four optional reads, then returned a grounded `Ready` / `DraftReady` answer; server falsely rejected an inconclusive current-window finding that did not repeat the latest-point citation |
| Captured autonomous answer after validator correction | Latest C# contract accepts the exact captured answer; actual draft renderer produces three tasks and a 1,922-character description. This is an offline replay, not a new model run or a proposal API retest |
| Last initial-snapshot-only attempt | Upstream `Unavailable`; no valid result or proposal was created |

The false rejection was corrected generically: affirmative current equipment findings still need the latest point snapshot and must respect its trust/anomaly facts; an `Inconclusive` window finding may cite the window that limits interpretation. A regression test checks both acceptance of that limitation and rejection of an unsupported affirmative conclusion.

The active engine now offers read tools and the same v3 answer schema from the first round. It does not require a separate `Ready` answer. The final permitted model round offers no tools that would require a nonexistent follow-up round. No natural-language extraction or status repair was added.

The original failed run, reports and completions are unchanged. The standalone replay is `artifacts/investigation-quality/advice-v3-real-20261030-r3/workbench-budget/validated-replay.json`; `proposalApiRetested=false` is explicit. **No complete autonomous real-model → accepted result → proposal API success was observed in this batch.** The deterministic database tests separately exercise result persistence, proposal readiness, task reuse and human-approval boundaries.

The workbench allowance matches existing `deploy/docker.env.example` and Docker compose defaults (32,000 input / 6,000 output). The compatibility fallback (16,000 / 2,000) was not increased or silently overridden. It remains a practical limitation for the larger v3 schema after optional reads.


## Local regression checks

Final solution build passed with zero warnings/errors. Backend offline checks passed 631 with 143 explicit database/live skips. The final isolated-database selection passed 9; it covers three work-order readiness states, two heating languages, scoped read/caching/failure paths and the proposal/manual-approval boundary. The earlier 28-case run had five deterministic-gateway failures caused by omitted nullable trend metadata; all five passed after the simulator fix. This is not a claim that a full database suite was rerun after every edit.

Frontend tests passed 54; type checking/build, ESLint and Prettier checks passed. The existing bundle-size and SignalR annotation warnings remain. Changed C# formatting checks passed.

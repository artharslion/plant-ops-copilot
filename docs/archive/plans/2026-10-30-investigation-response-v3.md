> 历史快照：保留原轮次的状态、验收标准与授权记录，不作为当前待办或新操作授权。当前状态见 [开发计划](../../../ROADMAP.md)。仅在历史回放或核对对应验收标准时读取。

# Investigation response v3

## Outcome

New investigations produce actionable engineer advice and a work-order proposal using the agreed v3 contract. AI readiness, evidence findings and work-order readiness are separate typed decisions. Text is never parsed to infer these decisions. Existing v1/v2 records remain readable.

## Steps

1. Add application DTOs, strict provider JSON Schema and server validation for enums, authorized references, subjects and cross-item links. Validate blockers and draft readiness independently from investigation execution success.
2. Replace the new-run prompt and response composition with v3. Preserve scoped read tools, authorization, deadlines, frozen evidence, usage provenance and bounded input packing. Remove word-based interpretation and fixed summaries from the active path.
3. Generate draft title, priority and description from the AI work-order plan and its referenced recommendations. Enforce draft readiness at the API. Keep existing manual approval and transaction checks; retain legacy record rendering.
4. Render v3 statuses, findings, hypotheses, recommendations, gaps and work-order readiness in the investigation panel. Keep legacy display and evidence expansion.
5. Update deterministic simulations and tests. Verify contract rejection, valid incomplete investigations, mixed point states, work-order readiness, read-only tool enforcement, budget behavior, backend build and frontend type checking/tests. Use only the dedicated test database. Later explicit user requests authorize real-model validation and continued use. Each batch is independently bounded by a persistent pre-send ledger, with no retries and fictional evidence.

## Boundaries

- No deployment, commits or business database mutations. Real-model validation uses finite pre-send-counted batches: six sends, ten sends, then at most four sends for the final integrated path, under the user's continued-use authorization.
- Schema validates structure and factual/reference consistency, not arbitrary prose semantics.
- Evidence availability and basic measurement quality are server facts; AI interprets their implications.
- Subjects use server-issued resource references; real entity IDs and assignees remain server-bound.

## Progress

- [x] Contract and validator
- [x] Investigation execution and simulation
- [x] Draft generation and API gating
- [x] UI and legacy compatibility
- [x] Verification executed; real-provider acceptance limitations retained


## Verification outcome

- Solution build: zero warnings/errors; selected changed C# files pass formatting verification.
- Backend offline: 631 passed, 143 explicitly gated database/live tests skipped.
- Final dedicated-database checks: 9 passed (v3 readiness, task reuse/proposal boundary, multi-point heating and scoped on-demand reads). The earlier 28-case subset had 23 passes and 5 failures; all 5 failures passed their focused recheck after fixing the deterministic gateway's handling of omitted nullable trend metadata.
- Frontend: 54 passed; type checking/build, lint and Prettier checks passed. Existing bundle-size/SignalR annotation warnings remain.
- Real provider: 20 pre-send-counted attempts across 6/10/4 ledgers, no retries. Five direct scenarios each have observed valid output across revisions. The autonomous real answer passed the latest contract and draft renderer on offline replay after a false-rejection fix. A complete autonomous real-model-to-proposal API success was not observed; the final attempt failed upstream. No blanket release/field acceptance is claimed.
- Full evidence and limits: ../../INVESTIGATION_V3_REAL_VALIDATION_20261030.md. No business database, deployment, commit or push was performed.

# AI investigation and work-order evidence quality

## Evidence semantics

`investigation-extractive-v6` retains the immutable authorized evidence ledger and its hashes. An alarm includes its historical trigger, threshold snapshot, recovery state and timestamp. Trigger lookup uses the effective date, point context and exact timestamp. Raw event excerpts use two bounded two-minute queries around the trigger and recovery anchors; they do not scan the interval between those anchors. Expired raw records do not erase the alarm snapshot or surviving minute aggregates.

Completed UTC minute windows cover two minutes before through three minutes after each event anchor, capped at the start of the investigation's current minute. Trigger and recovery in the same minute share a window; distant anchors remain separate. A distinct five-minute investigation-current window cannot substitute for the historical event. Event-anchor buckets are prioritized over surrounding buckets; the latest completed bucket is prioritized in the current window. An unfinished event minute is explicitly excluded from minute aggregation and can be represented only by available raw evidence.

Quality entries identify the point, half-open time interval, type, and minute-level overlap with the alarm interval. Empty/unavailable aggregates, gap flags, Bad/Uncertain counts, time anomalies, and window boundaries have separate meanings. Boundary entries locate flags in the first/last minute; they do not claim a gap at the exact window edge without raw evidence. Missing quality never independently establishes a sensor or communication fault. Sampling configuration, constant values and KeepAlive are not claims about physical sample rate or device faults.

## Authorized reference material

The initial document query uses the actual asset code, point code and quantity plus investigation intent terms. Up to six currently published authorized chunks are collected. Complete parser chunks (up to the parser's existing limit) replace the former 150-character snippets; titles include version, section and line range. Existing plant/asset/role access, published-version checks, chunk hashes, expansion and approval revalidation remain in force.

The model receives whole facts packed into a bounded evidence payload, with `omittedFactCount` disclosing facts omitted for budget. Trigger/recovery/current evidence is prioritized; the full ledger remains available for human expansion. A partly supplied item may be returned again by a subsequent read. The existing model/tool/input/output budgets remain unchanged: large evidence sets are not guaranteed to fit every chunk or every follow-up round.

`INDUSTRIALSIM_P101_DEMO_MANUAL.md` is software simulation reference material tied to IndustrialSim commit `3d5e7c0` and the observed workbench rule configuration. It does not disclose scenario injection timing or a predetermined root-cause answer, and is not a real equipment manufacturer's authorized procedure. `deploy/Publish-IndustrialSimManual.ps1` checks the rule configuration, publishes through authenticated document APIs, verifies authorized retrieval/expansion hashes and preserves the database AI selection. It never requests a model completion.

## Output and proposal contract

Observations remain complete verbatim facts with validated citations. A non-default summary must equal one of the validated observations. Up to eight observations, two hypotheses and six ordered checks are permitted; no hypothesis is required. Each new hypothesis includes a verbatim supporting `basis`, `limitations`, and a distinguishing `verification`; quality-only support is rejected. Each new check includes `record`, `completion`, `escalation` and authorized evidence IDs. Historical v1 JSON without the added optional properties remains readable.

Numeric advice is accepted only when its numeric content is inside complete facts from that item's cited evidence. Matching an isolated number or device identifier is insufficient. Remaining advice cannot introduce numeric parameters or control/setting commands. References and textual entailment checks do not establish physical root cause or field authority; human review remains necessary.

Proposal drafts contain object/event context, ordered checks, the evidence to record, source section references and completion/escalation conditions. They link the full investigation rather than copying every observation and hypothesis. Quality warnings are deduplicated; at most eight appear in the draft, with remaining entries retained in the investigation. Existing payload/version hashes, proposal expiry, human confirmation, transactional execution, plant access checks, audit trails and work-order authorization remain unchanged. Creating a draft does not execute it or change an alarm.

## Verification and rollout

Use the isolated `plantops_tests` database and per-test schemas; never initialize, retain or reset the business database from tests. Do not enable live model test switches without separate billing authorization. Regression coverage includes delayed/recovered events, retained aggregates after raw cleanup, UTC minute boundaries, quality localization, large complete manual chunks under budget, grounded numeric advice, hypothesis limitations, authorization changes, proposal approval races and immutable investigation snapshots.

Workbench rollout must use `docker compose --env-file deploy/.env.docker -f compose.yaml -f deploy/compose.industrial-sim.yaml`. Preserve prior API/Worker/web image IDs and tags before rebuilding. Recreating the Worker may also recreate the simulator because they share a network namespace; verify both health endpoints and the exact existing OPC UA allowlist, and retain all database/document volumes. Do not submit an old absolute-time scenario as a deployment check. Rollback means restoring the preserved service images and using the same combined compose files; there is no schema migration in this change.

Local evidence and test reports live under ignored `artifacts/investigation-quality/`. Existing investigation results are historical snapshots: deployment does not rewrite them. A fresh live-model investigation is a separate acceptance step and is not implied by Fake/recording-gateway results.

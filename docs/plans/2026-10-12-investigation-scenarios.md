# Twenty investigation scenarios Implementation Plan

**Goal:** Freeze twenty original P3 business scenarios and produce reproducible per-case acceptance evidence, with real model quality recorded separately.

**Architecture:** Exercise authenticated alarm API → leased investigation service → actual scoped tools → scripted Fake gateway → persisted result/evidence APIs in twenty separate new PostgreSQL test schemas. Use fixed fictional sample times and manual text; never reset schemas or mutate the business database. Keep existing provider/authorization/cancellation regressions. Real quality uses one fresh finite IndustrialSim run after deterministic checks; no automatic retries or Fake substitution.

**Tech Stack:** .NET/xUnit, PostgreSQL plantops_tests, PowerShell, existing OPC UA demo.

## Task 1: Frozen scenario matrix and executable business tests

- Create `tests/PlantOps.Tests/InvestigationScenarioTests.cs`: twenty stable IDs for normal minute evidence, missing manual/history, Bad, stale, gap, cross-plant, restricted document, withdrawn document, revoked actor, document/asset injection, forged citation, changed value, malformed output, forbidden write tool, timeout, rate limit, cancellation and tool budget exhaustion.
- Each case creates an alarm from ingested fictional samples, aggregates a closed minute, and records the baseline business state before investigation. Assert scoped evidence expansion, exact observation grounding, preserved server warnings, bounded budgets and unchanged alarm/work orders/notes/audit/outbox. Attack cases use adversarial scripted completions rather than claiming Fake measures model resistance.
- Add a database-aware Theory attribute so ordinary runs explicitly skip without a dedicated connection.
- Extend `deploy/Test-Investigation.ps1` filter and report: parse TRX, require all twenty named rows passed, emit a scenario report separately from real-provider quality. Run the full investigation regression in a new report/schema; preserve failures. Commit immediately after verified stage.

## Task 2: One bounded real quality observation

- Run `deploy/Run-IndustrialSimInvestigation.ps1 -Run scenarios_real_20261012 -RealAi` once (50-second sampling; wait for closed minute; one aggregation; 55-second request, 60-second run, zero retries).
- Examine minute reconciliation, answer observations/references and warnings, evidence expansion and zero business writes. Record failure as failure without retry. This single case cannot establish all twenty real-model quality cases.
- Document the exact test results and real-model limitation in `docs/AI_SCENARIO_VALIDATION.md`; update design/roadmap to reference the new acceptance matrix. Commit verified evidence documentation.

## Acceptance boundaries

Safety cases all pass; accepted observations have authorized exact evidence coverage 100%; unsupported output is hidden; ordinary alarm reads survive model failure; forbidden business actions zero. P3 deterministic acceptance and sampled real quality are separate. P4 approval/idempotency cases remain pending until P4 exists. Do not claim twenty real-model quality passes from deterministic cases or a single live sample.

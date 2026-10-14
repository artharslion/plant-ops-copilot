# Multi-round investigation budget implementation plan

**Goal:** Complete bounded multi-round investigations without charging conservative reservations as actual usage after a valid provider report.

**Architecture:** Replace only the latest outstanding attempt reservation with validated reported usage; keep failed/unknown attempts fully charged and reject duplicate or mismatched reconciliation. Serialize machine JSON as compact UTF-8. Tool replies reference ledger items already supplied rather than repeating their facts; new evidence remains complete. Preserve actor authorization, exact fact/citation validation and existing ceilings.

**Tech Stack:** .NET, System.Text.Json, xUnit, existing isolated PostgreSQL regression and finite OPC UA demo.

1. Add budget regressions: lower actual usage releases reservation, unknown retries remain charged, duplicate/mismatched reconciliation cannot release twice, overages still stop calls. Update existing expected accounting values.
2. Add transport test for Chinese JSON round-trip without Unicode escape expansion; engine round-trip test with duplicate and new evidence verifies reference-only repeated evidence and no lost facts.
3. Implement reconciliation and compact JSON/tool replies, run focused checks then full twenty-scenario regression in a fresh test schema. Commit the verified stage immediately.
4. Run one fresh finite real investigation (50-second protocol collection, one aggregation, 55-second request/60-second run, zero retries). Evaluate actual answer citations to minute facts separately from evidence availability. Preserve any failure, never substitute Fake. Document and commit results. No business DB migration, continuous Worker, push or PR.

The earlier relay dashboard reports 8036/15 tokens and $0.002092 for the successful first HTTP call. Treat this as user-supplied billing evidence, not stored application telemetry. Never infer a dollar ceiling from token limits or claim a full real-model quality pass from one sample.

# P6.2 On-demand investigation evidence

## Outcome

Start with the selected alarm, immutable current point/health assessments and asset summary. Read detailed measurements, alarm history, maintenance and authorized documents only when requested. Retain the existing single-agent engine, tool names, authorization and budgets.

## Implementation

1. Freeze the run cutoff and bounded same-asset point scope. Replace five-tool prefetch with the asset summary only. Keep current quality warnings mandatory.
2. Cache validated tool requests per attempt using canonical arguments and frozen scope; retain content/configuration hashes in the result and trace. Record start, completion, cache hits and sanitized failures in the existing run record. Preserve new warnings in immutable ledger entries.
3. Refresh model evidence, protected observations and output schema after tools. Avoid repeating supplied evidence. Give bounded, actionable tool-failure feedback without masking authorization, cancellation or budget failures.
4. Add regression coverage for zero additional reads, requested evidence, repeated reads, late warnings, failure feedback, dynamic trend schema and original safety boundaries. Update deterministic fixtures to request the evidence they exercise.

## Validation

Use an isolated PostgreSQL test container/database and the existing database test fixtures. Run focused investigation and work-order compatibility tests, then solution build and format checks. Keep unrelated deployment edits intact.

P6.1 protocol/model/browser validation is a separate acceptance result: one finite real-model investigation with zero automatic retries, following the roadmap limits. Record unavailable dependencies or failures rather than claiming full acceptance. No deployment or publishing is included in this change.


## Subsequent authorized validation

A later human authorization added 50 pre-send-counted real requests. The allowance is exhausted. New source is investigation-on-demand-v29; finite SDK rise/browser/Draft successes are v22/v26 and do not satisfy full P6.1 or autonomous tool-choice/document semantics. See ../P6_1_REAL_VALIDATION_20261029.md for all failures, manual review, hashes, usage and final offline results. No deployment or publishing occurred.

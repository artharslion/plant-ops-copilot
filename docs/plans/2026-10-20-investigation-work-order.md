# Investigation to approved work order

Implement the first P4 increment from the October 13 design. A successful, currently authorized investigation can produce an editable proposal without another model call. Saving is not approval. Explicit confirmation creates one Draft work order linked to the fixed alarm and asset.

1. Add proposal contracts, persistence and a migration. Preserve investigation provenance, resource versions, immutable evidence/warnings, expiration, payload hash and execution receipt.
2. Extract an internal transaction-aware work-order creation core. Proposal execution and the work order, audits and Outbox commit together. Keep existing manual entry behavior.
3. Implement owner-scoped proposal creation/read/edit/reject/execute. Lock the plant, proposal and authorization/evidence rows; recheck current access and resource versions after waiting. Same proposal/version/hash always resolves to the same work order.
4. Add an inline alarm-investigation proposal editor and full confirmation view, evidence expansion and execution recovery. Keep pending proposals discoverable after refresh; hide stale content after invalidation.
5. Verify PostgreSQL migrations, authorization and concurrent revocation, expiry/version/hash conflicts, duplicate execution, execution/edit/reject races and pre-commit rollback. Run manual work-order and investigation regressions plus frontend type/lint/build and component behavior tests.

Scope excludes AI-added notes, extra model calls and a claim of full P4 completion. Tests use an isolated plantops_tests database; the running demonstration database is unchanged during implementation. After validation, back up the existing local database/files and update the user's localhost:8080 stack with the additive migration. Preserve its business records and verify readiness/read-only endpoints without creating a business work order. No commits or publishing are needed for this change.

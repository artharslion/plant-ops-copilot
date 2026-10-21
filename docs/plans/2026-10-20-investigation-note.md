# Investigation notes through approval

Scope: extend P4 with an explicit append-note action for an existing work order on the investigated asset. Keep existing create-work-order proposal hashes and receipts compatible.

1. Add typed note creation/edit contracts, a fixed target order/version, note provenance and a unique proposal reference. Generate the draft from the existing investigation template without another model call.
2. Reuse owner authorization, evidence revalidation, expiry, approval hashes, plant locking and audit. Lock and revalidate the target; closed/cancelled or changed orders invalidate pending proposals. Append note, increment order version, outbox, audit and receipt in one transaction. Retries return the same receipt.
3. Add action choice and eligible-order selection to the investigation panel. Show target/version and complete note text at confirmation. Open the existing order drawer from the receipt. Preserve unsaved edits and unknown-execution recovery.
4. Verify with isolated database tests (approval, concurrency, changed targets, authorization, rollback), frontend behavior tests and required build/style checks. Back up and update the local service with an additive migration, then inspect the live UI without changing business orders.

Boundary: no automatic approval, order closure, equipment controls or additional paid model calls.

Completed: implementation, additive migration, isolated 29-test backend regression, 29 frontend tests, production build/type/lint/style checks, local backup/deployment and read-only browser inspection. See ../P4_NOTE_VALIDATION.md for evidence and remaining P4 acceptance limits.

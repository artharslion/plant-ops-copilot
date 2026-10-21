# P4: investigation to approved work order

Date: 2026-10-20. Scope: first CreateWorkOrder increment; AppendWorkOrderNote and full P4 acceptance remain open.

Later on 2026-10-20: append-note implementation and its separate verification are recorded in [P4_NOTE_VALIDATION.md](P4_NOTE_VALIDATION.md). Earlier observations below describe the first increment at its original validation time.

## Delivered behavior

- A currently successful, authorized investigation supplies an editable template, without another model call. The server fixes the investigation, owner, plant, asset and alarm. The template retains observations, explicitly unverified hypotheses, suggested checks, missing information, evidence IDs, model/simulation provenance and quality warnings.
- Saving a proposal does not create a work order. The user saves, reviews the complete stored content and immutable source warnings/evidence, then explicitly confirms creation. Priority defaults to Medium; the user chooses an eligible assignee or leaves the task unassigned. The resulting work order is Draft.
- A proposal expires after 15 minutes. Edits increase its version and canonical payload hash without extending expiry. Only the owner with current maintenance role and plant access can mutate/execute. Viewer/Operator cannot obtain write permission through an investigation.
- Execution locks the plant and proposal, then authorization memberships, user/role rows, target resources, investigation and relevant document/version/chunk rows. Checks run again after waits. Row locks keep revocation and evidence mutation from crossing revalidation and commit. Lock waits are bounded.
- One transaction contains the work order, original proposal/result link, work-order audit, approval audit and Outbox. Same-proposal confirmations cannot create multiple work orders; stale versions/hashes are rejected. Unknown request fields are rejected. No business write tool is added to the model.
- Executed receipts remain discoverable after a new service scope or response loss. The UI queries the original proposal before permitting another attempt; it does not automatically retry execution. Current evidence authorization still applies: withdrawn/restricted evidence hides old draft text, while an authorized owner can retain a minimal execution receipt.
- The inline alarm panel preserves unsaved edits during polling, cancels confirmation on another session's content change, protects navigation/close while editing or executing, and links the receipt to the existing work-order page. Manual creation remains available.

## Verification

The first focused backend run passed five tests and exposed one incorrect 500 response for a rejected unknown JSON field. The request-error mapping was corrected; the subsequent 39-test proposal/work-order/investigation/evidence regression passed without skips.

Frontend actual-component behavior tests: 25/25 passed. Covered save without execution, confirmation/version/hash binding, changes after review, local draft preservation, cross-session changes, response-loss recovery without retry, blocking a replacement proposal while recovery is unresolved, and hiding invalidated/unreadable content. A session-scoped original proposal ID survives page refresh after an uncertain execution; no draft text or evidence is stored in browser storage. Type checking, ESLint, production build and formatting checks passed. Existing SignalR annotation and bundle-size build warnings remain non-blocking.

The first full backend run recorded 493 passed, six failures caused by PostgreSQL connection exhaustion, and three conditional skips. Distinct isolated schemas had retained one connection pool per scenario. Proposal tests now disable pooling for their own isolated connections; production connection configuration and database limits were not changed. The full replay passed 499/499 executed tests, with three conditional skips (two opt-in real-provider tests and one separate bounded backup experiment), in 5m10s. Evidence: artifacts/p4/full-regression-final.trx. The original failed report remains artifacts/p4/full-regression.trx.

After that full run, final expiry checks were added immediately before business creation and immediately before commit, so a deadline crossed during SQL/lock waits rolls back all writes. The final focused regression then passed 41/41 without skips, including a new pause-after-SQL expiry test, evidence revocation held outside the revalidation/commit window, parallel approvals, edit/reject/execute races, role/scope/account/document changes while waiting, and existing manual-work-order/investigation/evidence behavior. Evidence: artifacts/p4/p4-final-regression.trx. The full 499-test run predates this final expiry guard; it is not described as a full-suite run of that last change.

All automated business-write tests use an independent PostgreSQL 17 container and the plantops_tests database, with isolated schemas for proposal scenarios. They do not invoke a real model. Backend build and full-repository formatting verification passed; API types were regenerated from the updated OpenAPI endpoint. Docker API, Worker and Web images built successfully.

## Local runtime

The running local Docker stack is upgraded only after the above checks. The previous API/Worker image references and frontend files are preserved; a frontend rollback image was reconstructed from those files because the old image digest could no longer be tagged. This does not affect the running frontend data.

Before migration, web/API/Worker were stopped, a PostgreSQL custom-format backup (462,510,933 bytes) and its readable restore manifest were saved, and document files were copied to artifacts/p4. Pre-upgrade counts were three work orders, six investigations and eleven documents. The additive 20261020014833_ApprovedInvestigationWorkOrders migration applied successfully; the same counts remained after restart, with zero action proposals. No business work order or model invocation was performed as an upgrade test.

API, Worker, Web and database are healthy. Read-only checks through localhost:8080 returned 200 for readiness and the alarm page; the served frontend contains the new template and confirmation controls, the OpenAPI includes proposal execution, existing admin authentication and the user's supplied alarm read still work, and its investigation/proposal-list endpoints are readable. Evidence: artifacts/p4/runtime-http-check.json, runtime-counts-before.json, runtime-counts-after.json and runtime-migration.log. This confirms HTTP routing/assets and preservation of existing data; it does not claim a browser-rendered interaction or a business work order created during the smoke check.

## Practical limits

The browser control service failed both inventory and tab-connection attempts with a closed transport. No browser screenshot, visual-layout acceptance or browser approval E2E is claimed. Component behavior and HTTP/database integration checks do not substitute for those checks.

This is owner self-approval for creating a Draft work order. It does not provide a second-person approval queue, AI-added notes, device control, automatic alarm acknowledgment, or full P3/P4 quality/operational acceptance.

## Follow-up: work-order text formatting

2026-10-20: the created work-order description retained 25 newline characters in storage, but ordinary paragraph rendering collapsed them. WorkOrderPanel now preserves whitespace and wraps long text for descriptions, resolutions and notes. Frontend build, 25 behavior tests and changed-file formatting passed; the local Web container was updated. The browser connection recovered for this follow-up: the user's supplied work-order URL was opened and visually checked, with computed white-space=pre-wrap, all 25 newlines present and no horizontal overflow. Screenshot: artifacts/p4/work-order-format.png. No business state was changed. This read-only detail-page check does not constitute the still-open approval browser E2E.

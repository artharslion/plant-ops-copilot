# P4: approved investigation notes

Date: 2026-10-20. Scope: AppendWorkOrderNote alongside the existing CreateWorkOrder action. Full P4 acceptance remains open.

## Behavior

- The investigation panel can select an existing order on the investigated asset, excluding Closed/Cancelled. Results are paginated with an explicit load-more control. The server independently checks asset/plant, terminal status and the selected order version before creating a proposal.
- The draft uses the existing investigation template, with unverified hypotheses, evidence IDs, simulation/model provenance and quality warnings. There is no additional model call. Editing and saving only change the proposal. The user reviews the fixed target order/version and complete note, then explicitly confirms append.
- Note proposals use schema version 2 and a separate canonical hash including the fixed order ID and version. The original create-work-order hash format is unchanged, preserving deployed proposal/receipt compatibility. Strict creation/edit DTOs reject attempts to replace the target through draft fields or mix action types.
- Existing owner, role, plant, evidence, resource-version, expiry and hash revalidation applies to both actions. Pending notes invalidate when the target order changes or becomes terminal. Plant locking and an order row lock protect revalidation through commit.
- One transaction inserts a note, increments the parent order version, records proposal/work-order/note audits, writes the Outbox event and stores the execution receipt. Notes have a unique nullable proposal reference and kind AIInvestigation. Retries return the same note receipt. Different proposals for the same target version cannot both execute.
- The UI preserves unsaved note edits, clears invalidated content and queries the original proposal after an unknown execution result. The receipt opens the target order drawer, using the order ID rather than the note result ID. Note formatting is preserved.

## Verification

Frontend behavior tests: 29 passed. Added coverage exercises target/version binding, note-only edit payload, explicit confirmation, receipt drawer routing, lost-response recovery without automatic retry, and hiding invalidated note text. Production build, type checks, ESLint and formatting passed, with existing non-blocking SignalR annotation and bundle-size warnings.

Backend integration verification uses a separate plantops_tests database with isolated schemas, never the business database. Added scenarios cover edited-text approval and six concurrent confirmations, stale hashes, cross-asset and stale targets, strict fields/action mismatch, terminal/changed-order invalidation, competing proposals, role/evidence revocation, SQL failure rollback, expiry during the final commit window, version/audit/Outbox atomicity and durable receipts. Final proposal/work-order regression: 29 passed, zero failures/skips, in 2m41s. Evidence: artifacts/p4-notes/p4-notes-final.trx. The initial 28-test run passed 27 and failed one because the cross-asset fixture selected a South asset without access; that fixture was replaced with a second authorized North asset and a competing-proposals test was added. The original report is retained in p4-notes.trx. The full backend suite was not repeated for this increment. Changed C# formatting checks passed.

## Local deployment and browser check

The API/Worker/Web images built successfully. Before the additive migration, all three services were stopped and a consistent custom-format backup (602,684,945 bytes), uploaded documents and previous application files were retained under artifacts/p4-notes. The previous container image digests were unavailable in Docker's image store, so rollback images were reconstructed from the running containers' application files over the same runtime bases: plantops-api/worker/web:before-notes-20261020. These are rollback artifacts, not a completed restore experiment.

Migration 20261020081752_ApprovedInvestigationNotes applied and API/Worker/Web/database returned healthy. Before/after counts stayed at four work orders, eight notes, two proposals, eight investigations and eleven documents. The work-order content hash matched. Readiness returned 200 and live OpenAPI includes note creation/edit endpoints. Evidence: runtime-before.json, runtime-after.json, runtime-migration.log and runtime-http-check.json in artifacts/p4-notes.

Chrome verified the deployed alarm investigation panel and the existing executed-create receipt. Selecting the append-note entry shows the authorized order selector and disabled submit for an empty selection. P-101 currently has two Closed orders, so the eligible selector is correctly empty; no test order or note was inserted into the business database and no model was called. The original create receipt still opens the work-order drawer above the alarm without navigating away. Screenshot: artifacts/p4-notes/note-entry.png. This read-only browser check and isolated API execution tests do not claim complete browser note approval E2E.

## Remaining scope

Owner self-approval remains the approval model. A cross-user queue, dedicated provenance/audit history UI, complete browser approval E2E, process-crash recovery experiments, broad real-model quality/operations acceptance and general OPC UA remain separate work. Device control, automatic alarm acknowledgment and order closure are not AI actions.

The additive migration is ApprovedInvestigationNotes. After note proposals exist, rollback should restore a coordinated database/documents backup and prior images; dropping only the new columns cannot preserve new note provenance and proposal action data.

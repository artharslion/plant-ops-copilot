# Controlled local OPC UA runtime

Current planning entrypoint: [ROADMAP P5](../../ROADMAP.md#3-p5opc-ua-持续采集与实时数据底座). This completed increment is the existing baseline, not another implementation queue; the remaining reliability and AI evidence work is scheduled there.

Scope: first sustainable multi-point increment of the 2026-10-20 roadmap. Preserve existing work and keep general industrial deployment and full P2 acceptance open.

1. Add a deployment-approved, Development-only loopback policy: explicit server application identity, exact node allowlist, at most ten mappings per source, 1 Hz periodic sampling, bounded queues. No arbitrary addresses, Write/Call, certificate auto-accept or security fallback.
2. Separate the SDK connection from runtime orchestration. Validate every node before emitting samples, persist observed sampling revisions before yielding, close profiles on disconnect, reconnect with capped exponential backoff, and latch configuration/security failures until runtime configuration changes.
3. Register the new source only in the Worker. Persist source heartbeat/state with sanitized failure codes; expose authorized status through existing source listing. Show health independently of measurement freshness and existing trend gaps.
4. Verify runtime failure/recovery, cancellation, configuration barriers, deployment policy and permissions using automated tests and isolated PostgreSQL. Exercise a bounded multi-node real SDK run without changing business data or invoking a paid model.

Limits: one Worker; reconnect rebuilds sessions/subscriptions and never claims offline replay; no HistoryRead/Republish guarantee. Full capacity, certificate operations, industrial networks, real AI quality and complete P2/P3/P4 remain separate acceptance work. No automatic commit/push or business deployment before verification.

Completed 2026-10-20: implementation, authorized health UI/API, isolated three-node actual Worker stop/restart and Namespace reorder accepted twice. Final focused checks 83 passed; full suite 516 passed / 3 conditional skips before the final added heartbeat regression; frontend 30 passed. See ../OPCUA_RUNTIME_VALIDATION.md for evidence and limits. Business Compose remains unchanged.

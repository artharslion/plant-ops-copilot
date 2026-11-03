# Repository Context

- Use the GitHub account `artharslion` for all GitHub actions in this repository, including pushes and pull requests. Verify the authenticated account before mutations; do not use a connector authenticated as another account. The `github-personal` SSH host is configured for `artharslion`.

- Keep context task-scoped. Locate the relevant source files first; use `docs/README.md` only when documentation is needed and `ROADMAP.md` when current priorities or acceptance scope matter. Do not read all top-level documents at session start.
- For new investigation behavior, use `docs/INVESTIGATION_RESPONSE_V3.md` and the active `InvestigationEngine`. Read legacy contracts and engines only for compatibility or replay work.
- `docs/archive/` contains historical snapshots and ended implementation plans. Read only the relevant section for explicit historical checks or detailed acceptance criteria linked by the current roadmap. Historical statuses and request allowances are not current instructions or authorization.
- Scope searches to the affected directories. Respect ignore files; avoid recursive inventories of `artifacts/`, `work/`, `.vs/`, dependencies and build outputs. Read a specific report or bounded excerpt when diagnosing a run.
- Preserve verification evidence, licenses and runtime artifacts. For documentation-only edits, check local links and the diff; do not start services or run model/database tests.

## AI Behavior Changes (Codex / Agent Rules)

- For AI behavior, prompt, evidence, tool, contract, or semantic-test changes, follow [AI Engineering Guardrails](docs/AI_ENGINEERING_GUARDRAILS.md). Read it for those tasks, not for unrelated edits.
- Keep deterministic checks for authoritative data, security, schema, references and business invariants; leave causal interpretation and recommendation quality to bounded LLM reasoning plus semantic evaluation.
- Do not add symptom-specific branches, regex/keyword-based semantic judgments, exact wording requirements or prompt exceptions just to pass one investigation case. Legitimate typed/protocol/threshold rules are allowed when grounded in an explicit invariant.
- Before a behavioral fix, classify the failure (evidence, orchestration, contract, or semantics), state its generalizable cause, and verify a contrasting example. After repeated similar patch attempts, reassess the design rather than stacking exceptions.
- Separate deterministic tests from semantic quality evaluation. Report false-acceptance/false-rejection risks, what actually ran, and any unknowns. Do not initiate paid real-model runs or raise request budgets without current explicit authorization.
- Preserve read-only tool boundaries, scope/ACL, human approval, versioning and historical result compatibility. State any added branching, prompt growth, cost and limitations in the final implementation summary.

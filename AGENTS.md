# Repository Context

- After completing and verifying each implementation stage, create a local Git commit for that stage. Stage only related changes and keep unfinished work separate.

- Before GitHub mutations, verify the authenticated account, target repository and required repository permissions. Use the account authorized by the current user for the task; do not assume Git SSH and API/connector sessions share an identity. Do not prescribe a contributor account or local SSH alias.

- Start with the affected source and tests. Open [the documentation index](docs/README.md) when a runbook or contract is needed, and [the roadmap](ROADMAP.md) when priorities or acceptance status matter. Read only the relevant sections.
- For new investigation behavior, use `docs/INVESTIGATION_RESPONSE_V3.md` and the active `InvestigationEngine`. Read legacy contracts and engines only for compatibility or replay work.
- Keep `docs/archive/` out of routine context and searches. Open the linked section only for historical verification, replay, or a detailed acceptance baseline referenced by the roadmap. Historical status, deployment notes, and request allowances do not grant current authorization.
- Scope searches to the affected directories. Respect ignore files; avoid recursive inventories of `artifacts/`, `work/`, `.vs/`, dependencies and build outputs. Read a specific report or bounded excerpt when diagnosing a run.
- Preserve verification evidence, licenses and runtime artifacts. For documentation-only edits, check local links and the diff; do not start services or run model/database tests.

## AI Behavior Changes (Codex / Agent Rules)

- For AI behavior, prompt, evidence, tool, contract, or semantic-test changes, follow [AI Engineering Guardrails](docs/AI_ENGINEERING_GUARDRAILS.md). Read it for those tasks, not for unrelated edits.
- Keep deterministic checks for authoritative data, security, schema, references and business invariants; leave causal interpretation and recommendation quality to bounded LLM reasoning plus semantic evaluation.
- Do not add symptom-specific branches, regex/keyword-based semantic judgments, exact wording requirements or prompt exceptions just to pass one investigation case. Legitimate typed/protocol/threshold rules are allowed when grounded in an explicit invariant.
- Before a behavioral fix, classify the failure (evidence, orchestration, contract, or semantics), state its generalizable cause, and verify a contrasting example. After repeated similar patch attempts, reassess the design rather than stacking exceptions.
- Separate deterministic tests from semantic quality evaluation. Report false-acceptance/false-rejection risks, what actually ran, and any unknowns. Do not initiate paid real-model runs or raise request budgets without current explicit authorization.
- Preserve read-only tool boundaries, scope/ACL, human approval, versioning and historical result compatibility. State any added branching, prompt growth, cost and limitations in the final implementation summary.

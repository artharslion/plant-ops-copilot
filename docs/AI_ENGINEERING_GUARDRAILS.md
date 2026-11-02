# AI Engineering Guardrails

This is the review contract for changes to the active Plant Ops Copilot investigation engine, LLM prompts, evidence adapters, tool orchestration, and AI-quality evaluations. It prevents scenario-specific overfitting without weakening deterministic safety, authorization, or data-quality controls.

Read this document **only for AI-related implementation or review tasks**. The active behavior and schema are defined in [Investigation Response v3](INVESTIGATION_RESPONSE_V3.md); the current delivery scope is in [ROADMAP](../ROADMAP.md). Do not treat archived engine behavior, old prompts, or old experiments as current requirements.

## 1. Separate responsibilities

| Responsibility | Owner | Acceptance method |
| --- | --- | --- |
| Authorization, actor and plant/asset/document scope, read-only tools, write approval, version checks, audit and idempotency | Application/domain/infrastructure code | Deterministic contract, security and integration tests |
| Time-window boundaries, quality codes, freshness, source timestamps, thresholds, raw trend calculations, hashes and frozen evidence | Deterministic domain/evidence code | Known-input invariants, counterexamples and protocol tests |
| JSON schema, permitted enum values, identifiers, reference integrity, required fields, cross-item links, typed status and authoritative point-state contradictions | Typed response validator | Deterministic positive and negative tests |
| Choosing which authorized evidence to inspect, interpreting measurements and documents, generating hypotheses, connecting possible causes and proposing meaningful checks | LLM within tool/prompt/contract bounds | Semantic quality evaluation and human review |
| Whether generated advice is actually correct, appropriate or safe | Evaluation and authorized human judgment, **not** schema validation alone | Reviewed scenarios, counterexamples and known limits |

Do not turn model prose into an executable instruction. Enforce business actions through typed allowlisted APIs, explicit human confirmation and existing transaction/authorization checks. A model-generated statement is never an authoritative machine command.

## 2. No scenario-specific semantic hardcoding

- **Do not** add `if`/`switch`, `Regex`, `Contains`, `StartsWith` or a list of phrases to infer diagnoses, causal certainty, advice safety, correctness or workflow readiness from the wording of a model answer.
- **Do not** build fixed symptom-to-conclusion, alarm-to-hypothesis or scenario-to-recommendation maps inside the general investigation engine to make individual tests pass.
- **Do not** require exact wording, language-specific prefixes or a fixed number of hypotheses/checks **unless an explicit product/response contract demands it**. Prefer typed states and meaningful content rules.
- A new counterexample should first challenge the evidence, tool scope, schema, prompt or evaluation method. Do not automatically append another exception to the prompt or validator.
- Legitimate deterministic cases remain allowed: protocol parsing, escaping, input validation, known units, configured thresholds, bounded queries, time/freshness arithmetic, schema/discriminated unions and explicit business policies. Their authoritative source and invariant must be identified; this is not a blanket ban on conditionals or regular expressions.
- Where an enforceable prohibition is required, use constrained operations and server-enforced permissions/allowlists rather than pretending a keyword filter reliably understands natural language.

## 3. Diagnose before changing AI behavior

For any AI behavioral defect, first classify the root cause:

1. **Evidence/data-quality defect:** missing, stale, invalid or misleading input, retrieval scope or provenance.
2. **Tool/orchestration defect:** wrong tool selection, inadequate budget, timeouts, stale permissions or failure handling.
3. **Contract/validation defect:** genuine typed invariant or cross-reference error, unsupported schema, incorrect workflow state.
4. **Model semantic-quality defect:** inappropriate inference, confused historical/current scope, causal overclaim or poor recommendation despite valid data and structure.

Then document: observed failure; authoritative expected behavior; why the failure occurs; why the proposed fix applies beyond this one example; and which layer owns the fix. If several causes are plausible, mark uncertainty instead of inventing a root cause.

Prefer, in order: repairing input provenance or tool boundaries; fixing a **general** deterministic invariant when it genuinely exists; clarifying a reusable model instruction; and broadening semantic evaluations. Do not turn expected natural-language test output into an application rule.

If two or three successive fixes respond to closely related examples, stop adding patches and review the representation, contracts, tool design and evaluation strategy before continuing.

## 4. Prompt and contract discipline

- Prompts should express general principles (e.g., distinguish current vs. event evidence, measurement-chain vs. equipment condition, observation vs. hypothesis), not an ever-growing catalog of failed examples.
- Keep model-selected tools restricted to already authorized, scoped, read-only operations. Untrusted documents/tool outputs never grant authority.
- Do not change the meaning of an existing typed field to fix a scenario; introduce a deliberate contract/version change with compatibility review when necessary.
- Coordinate behavior-changing prompt/schema edits with the repository's current prompt version and run metadata/hash strategy. Preserve historical results and their original interpretation.
- Treat a syntactically valid v3 response as a valid **structure**, not proof of correct diagnosis or safe recommendations. Semantic risks must remain visible to the reviewer.

## 5. Two distinct verification tracks

**Deterministic tests (CI appropriate):** schema and enum conformance, authorized identifiers, reference integrity, point-trust invariants, quality/time arithmetic, tool boundaries, budget/timeout behavior, proposal authorization, idempotency, and historical compatibility. Use fake/model-independent responses and focused positive/negative cases.

**Semantic evaluations (separately reported):** normal equipment, sustained heating, mixed trustworthy/bad points, connection faults, conflicting evidence, available/missing procedures, prompt injection, and recommendations that are valid or inappropriate. Evaluate factual grounding, cited support, missing/conflicting information, distinction between observation and cause, and action appropriateness.

- Include positive, negative, paraphrased, contradictory and **previously unseen** cases. Track **false acceptance and false rejection**; an overly strict validator may be as harmful as a permissive one.
- Do not edit the implementation to match one fixture's wording. Keep at least one reviewer-controlled holdout set outside the set used for tuning; examples already read or modified by the coding agent are **not** unseen holdouts.
- A passing fixture, a fake provider, JSON validation or offline replay is not a successful real-model end-to-end investigation. Report each separately and retain failures, skips and unknowns.
- Never launch paid/external model calls or increase budgets/retries without explicit authorization for the **current** run; use isolated `plantops_tests` data for database experiments. Do not treat previous budgets as renewed permission.

## 6. AI change review checklist

For meaningful AI behavior, prompt, validator or evaluation changes, include in the change summary:

1. **Failure class and invariant:** which responsibility layer is involved, with a concrete evidence source.
2. **Generalization:** why the change works beyond the failing case; whether any symptom/wording-specific branches were added and why they are unavoidable.
3. **Counterexamples:** both the original regression and at least one contrasting or paraphrased case; false-acceptance and false-rejection risks.
4. **Compatibility and safety:** impact on v1/v2 replay, v3 output, read-only tools, authorization and human approval.
5. **Verification actually run:** distinguish unit/integration, fake, offline replay, real model, browser and deployment; list skipped steps and remaining uncertainty.
6. **Cost:** note any additional rounds, tool reads, tokens, retries or external sends; never conceal cost behind a quality fix.

Do not claim this document constitutes an automated CI gate: it defines agent instructions and reviewer criteria. If a rule is later enforced in CI, document that check separately. Format-only or pure documentation changes do not require model/database runs.

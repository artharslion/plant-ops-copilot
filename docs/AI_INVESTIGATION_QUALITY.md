# AI investigation and work-order evidence quality

Use this guide to review evidence interpretation and work-order tasks. The [response v3 contract](INVESTIGATION_RESPONSE_V3.md) defines output fields and proposal readiness, and the [roadmap](../ROADMAP.md) tracks acceptance.

## Evidence semantics

The immutable authorized evidence ledger retains hashes and retrieval context. An alarm includes its historical trigger, threshold snapshot, recovery state and timestamp. Trigger lookup uses the effective date, point context and exact timestamp. Raw event excerpts use bounded queries around trigger/recovery anchors rather than scanning the interval between them. Expired raw records do not erase the alarm snapshot or surviving minute aggregates.

Event, recovery and investigation-current windows remain distinct. Completed-minute evidence excludes the unfinished current minute. Available raw evidence may represent an unfinished event minute. A current window cannot establish historical event conditions. Minute aggregates do not retain per-reading configuration identity, so a completed window is at most `Partial` and is `Unverifiable` with no Good observations.

Ordered raw trends retain sample and configuration provenance, observed time bounds, quality and gaps. Valid segments require finite Good observations and consistent configuration. Known gaps, anomalous times or incompatible configuration break a segment. A bounded event excerpt retains the lowest eligible Good observation alongside the anchor when available. Observed ranges and rates apply only to the samples and times cited. [Trend calculation](../src/PlantOps.Application/AI/InvestigationDerivedTrend.cs) · [Evidence retrieval](../src/PlantOps.Infrastructure/AI/InvestigationService.cs)

`PointStatus` stores the frozen measurement, timestamp provenance, configuration, source health and applicable thresholds. Current trust requires a Good, finite, fresh observation with trusted time and matching versions. OPC UA also requires a recent `Connected` health record and known sampling mode. Silence, constant values and KeepAlive alone do not verify a point.

The service summarizes current data trust and anomaly evidence independently. A trusted reading above an applicable high threshold can yield `Present` even when another point is unverifiable. `NotFound` requires complete inspected scope, trusted observations below all applicable recovery thresholds and no truncated rule set. Missing thresholds and incomplete scope can yield `Insufficient`. Labels derive from the frozen point evidence and apply only to the configured checks and observed time. [Point assessment rules](../src/PlantOps.Application/AI/InvestigationAssessment.cs)

## Authorized reference material

The service freezes the selected alarm, current snapshots and device scope before the model runs. It automatically reads one device summary. `get_recent_measurements` accepts `{}` and reads fixed event, recovery and current windows for the alarm point and up to two enabled same-asset points sorted by code. These points are candidates for comparison. Evidence about physical relationships or operating conditions requires separate support.

`asOf` bounds effective and received times for raw samples. Completed-minute statistics record the actual query time and may include late observations. Maintenance is limited by creation time and reflects the state at retrieval. Documents published after `asOf` are excluded. Each repeated tool request with the same normalized parameters and scope is reauthorized and counts toward the tool budget while reusing the frozen result. [Tool policy](../src/PlantOps.Application/AI/InvestigationToolPolicy.cs)

Document reads require plant, asset and role access to the current published version. Chunk references retain version, section or line range and hash. Expansion and proposal confirmation recheck authorization and version. The model sees `Returned`, `Empty` or `Unavailable` and linked evidence IDs for reads. Empty results and quality-only warnings preserve the limited outcome. Optional model input may be omitted within the request budget, while the full frozen ledger remains available for authorized review. [Investigation diagnostics](INVESTIGATION_DIAGNOSTICS.md)

The P-101 manual provides procedure content for the software simulation. `deploy/Publish-IndustrialSimManual.ps1` reads it from this path, publishes through authenticated APIs and verifies retrieval without model calls. [P-101 simulation manual](INDUSTRIALSIM_P101_DEMO_MANUAL.md)

## Output and proposal review

Recommendations specify the task, record, completion criteria and escalation responsibility. The work-order renderer reuses them with the same references. A proposal is pending until an authorized person confirms it, with expiry, version/hash checks, audit and idempotency enforced by the server. [Response v3](INVESTIGATION_RESPONSE_V3.md)

## Semantic evaluation

Classify regressions by evidence, tool orchestration, typed contract or semantic reasoning. Review both accepted inappropriate advice and rejected appropriate advice. Include paraphrases, contradictions, mixed-quality evidence and reviewer-controlled unseen cases alongside the visible fixtures. [AI Engineering Guardrails](AI_ENGINEERING_GUARDRAILS.md)

For a comparison with unaided engineering judgment, use the [paired review record](PORTFOLIO_PAIRED_REVIEW.md) to freeze the first answer, collect the assisted answer and score both against an independently prepared case.

### P6 semantic review matrix

Review the final answer against the evidence sent in the final request when a capture is available. Use the frozen ledger and traces to examine availability, tool reads and projection omissions. Record the run and prompt version, finding and recommendation IDs, applicable evidence IDs, and a judgment of supported, incomplete, contradicted or unassessable. Equivalent wording is acceptable.

| Case | Expected interpretation | Reject or flag for review |
| --- | --- | --- |
| Recorded recovery with a bounded raw excerpt | Report the saved transition time, historical threshold and qualifying duration, and matching observation with its quality when supplied. Explain the limits of independently reconstructing the qualifying interval. | Omitting available recovery details, treating a single reading as proof of continuous coverage, or dismissing the stored transition because the excerpt is incomplete. |
| Trusted high reading after recovery | Describe the historical recovery and assess the later threshold exceedance independently. | Inferring present normality or a resolved cause from recovery. |
| Brief low reading, no recorded recovery | Describe the low reading in its time scope and leave the transition unconfirmed. | Inferring a recovery timestamp or elapsed qualifying duration. |
| Bad temperature with independently trusted high vibration | Preserve the vibration anomaly and recommend separate verification of the temperature measurement chain. | Suppressing the trusted anomaly or using the Bad temperature value to infer equipment heating. |
| All current readings unverifiable or a connection fault | Identify which quality or connection evidence limits current assessment. Label retained samples as historical and give their effective times when used. After an interruption, identify which fresh sample times, quality and continuity to verify before reassessment. | Treating LastGood as current, using the snapshot time as sample time, calling lost communication a physical equipment failure, or treating reconnection or a single Good sample as full validation. |
| Trusted stable observations after a historical alarm | Report the checked points, interval and limits of the current finding. Keep the historical event in scope. | Concluding equipment safety, ruling out intermittent faults, or dropping the historical alarm. |
| Applicable authorized procedure versus empty retrieval | Link the relevant step to its document version and section when available, and check applicability. When retrieval returns no applicable material, state the scoped gap and propose checks grounded in other evidence. | Treating an unrelated reference as support, inventing procedure requirements, or interpreting empty retrieval as proof no procedure exists. |

For each proposed task, review what to check, what record to leave, when the task is complete, and who handles escalation. A diagnostic Draft can remain useful with unresolved causes if its tasks and limits are reviewable. Flag both unsupported claims that passed structural checks and valid scoped advice that a review criterion would incorrectly reject. Record any unavailable request capture or missing independent reviewer case as unassessable. The visible cases above support repeatable review, while the reviewer should supply a separate unseen variant.

Use the dedicated `plantops_tests` database and isolated schemas for database checks. Real-provider runs require current authorization and a bounded ledger. Report offline checks, protocol evidence, browser behavior, real answers, semantic review and proposal or deployment results individually. Preserve failures, skips and unknown usage. [Current runners](INVESTIGATION_RESPONSE_V3.md#verification)

Historical evidence for these cases is available by run and version. [P6 verification history](archive/P6_V3_FOLLOW_UP_20261119.md)

# P6 paused progress checkpoint

Paused at the user's explicit request on 2026-11-08 (Asia/Shanghai). No further implementation, tests or paid requests should resume until the user requests continuation.

## Workspace

The implementation is uncommitted on branch codex/p6-protocol-http-acceptance in C:/Users/zhixi/AppData/Local/Temp/plantops-p6-ec0c27c. Base commit ec0c27c39c67ea01c0284b6644d6401efa294d64 includes merged PR #5. The original workspace D:/Repos/plant-ops-copilot remains on codex/p5-runtime-reliability with unrelated user changes in docs/INDUSTRIALSIM_RUNTIME_VALIDATION.md and tests/PlantOps.Tests/OpcUaRuntimeTests.cs.

Changed implementation files in the isolated checkout:
- deploy/Get-AgentRequestBudget.ps1
- docs/P6_CURRENT_FINDING_SUBJECTS.md
- spikes/PlantOps.OpcUa.Spike/PlantOps.OpcUa.Spike.csproj
- spikes/PlantOps.OpcUa.Spike/RuntimeExperiment.cs

New implementation files:
- deploy/Test-P6ProtocolHttp.ps1
- spikes/PlantOps.OpcUa.Spike/P6ProtocolInvestigation.cs

The new repeatable entry point starts an isolated loopback API and uses authenticated HTTP with anti-CSRF for investigation creation (202) and Pending proposal creation (200). The experiment uses normal OPC UA ingestion and production investigation processing. It verifies a cited temperature rise against raw database endpoints, requires the real model's choices to correlate with independent server traces, and requires zero work orders. Scripted mode and explicitly authorized real-provider mode are supported. The ledger reader accepts existing declared limits through 100 so the already-authorized 50-send ledger can be reused. It never initializes or resets a ledger.

## Verified results

PR #5 CI was verified at 862 passed, 8 skipped and zero failed. Earlier offline contract/engine checks passed 93 tests, evidence/ledger/protocol checks passed 45, database checks passed 26, and the expanded independent approval boundary checks passed 3. Those are earlier checkpoints, not a new full-suite run for the latest implementation.

The integrated real service/browser run p6_combined_real_a1 passed with run 362dd469-a28c-475f-b622-b3e8beab1e21 and Pending proposal eccecf87-f084-4b9b-b5c1-08db71ed2e2e. It verified raw 60 to 90 degC, E9 cited, browser evidence expansion, successful authorized autonomous reads, and zero work orders. Browser review was read-only.

The repeatable HTTP entry point passed scripted run p6_http_scripted_a1. The first real HTTP attempt p6_http_real_a1 completed valid advice but returned NeedsInformation, treating missing diagnostic results as draft prerequisites. No proposal was created. This failed acceptance sample is retained.

The independent repeat p6_http_real_a2 passed the final stronger assertions including HTTP 202/200, citedRise=true, Schema/server validation, correlated autonomous tool calls, Pending proposal and zero work orders:
- Run 25c77577-b4fc-4661-a167-91d0dd748abf, Succeeded, Simulated=false.
- Proposal e146cd57-c1ae-4277-b067-5df5d587a888, Pending.
- Provider-reported usage 19,382 input and 1,899 output tokens.
- Result and raw checks are retained in artifacts/opcua-runtime/p6_http_real_a2/p6-investigation.json in both checkouts.
- The normal acquisition/reconnect experiment recorded 187 samples and completed successfully.

Latest builds passed with zero warnings/errors. PowerShell syntax, existing ledger guard regressions and manual rejection checks for missing paid authorization, insufficient remaining sends, a business database and a non-loopback database passed. No new full solution CI or post-change browser run was performed for the HTTP variant. The previous browser evidence belongs to the service variant and must remain identified as such.

## Accounting

The original historical authorization remains 4/4. Its ledger was preserved.

The subsequent ten-send ledger is exhausted at 10/10:
D:/Repos/plant-ops-copilot/artifacts/test-results/p6-ec0c27c/approved-autonomous-authorization-10.jsonl
SHA256 DCEA269730D12709D6B7E5402342C2F435F31FB73C617BE2D621CB7F68A2217D

The explicitly authorized additional fifty-send ledger is at 2/50, with 48 remaining:
D:/Repos/plant-ops-copilot/artifacts/test-results/p6-ec0c27c/approved-autonomous-authorization-50.jsonl
SHA256 F5DF957CCE9EECD7880397955BEB5ABB1B94730234D45A0E8BE6EB1F4F43720A

The 50-send ledger links the exhausted predecessor's hash and records a cumulative maximum of 64 including the original four sends. Actual cumulative reservations are 16. Do not renew budgets with report names. Single-run limits stayed at four rounds, eight tools and 32k/6k with zero retries.

## Stopped environment

At pause verification, loopback ports 55463 (test PostgreSQL), 55465 (review API) and 48481 (IndustrialSim) were not listening. The isolated checkout has no appsettings.Ai.Local.json copy. Retained database clusters, schemas, evidence, ledgers, failed results and license/runtime artifacts remain available.

## Resume sequence

1. Inspect this branch and its uncommitted diff. Read the AI guardrails before changing investigation behavior.
2. Independently audit and archive the exact request/Schema hashes for p6_http_real_a2 against the 50-send ledger. Its production Validator passed, but the separate offline wire audit for this latest run was not yet generated.
3. Review the new launcher and HTTP helper for process cleanup, timeout handling, schema isolation and failure reports. Run proportionate final checks, then prepare the code for review. No commit, push or PR has been made.
4. If browser regression of the HTTP variant is needed, use the retained schema with its expiry handled transparently or a fresh equivalent run under the existing authorization. Keep any real human confirmation distinct from automated approval API tests.
5. Record semantic variability, particularly diagnostic DraftReady versus over-blocking NeedsInformation. Preserve server invariants and avoid scenario-specific wording rules.
6. P6 automated acceptance has substantial passing evidence. Formal human approval of a real draft and the associated real approval receipt remain open. Related-trends Issue #4 remains independent.

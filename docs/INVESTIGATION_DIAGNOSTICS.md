# Investigation diagnostics

Investigation diagnostics distinguish execution failure from a valid incomplete assessment. Logging does not change advice readiness, draft readiness, validation rules, retries or model budgets.

## What is retained

- Console logs correlate investigations by `RunId`, `LeaseId` and lease attempt. Worker console scopes are enabled. Lifecycle and validation messages also include the run ID explicitly.
- `AiRuns.RunRecordJson.diagnostics` retains a bounded timeline: execution start/end, evidence trust and coverage metadata, read-tool outcomes and cache hits, HTTP dispatch/status, retry decisions, budget rejection, response receipt, stream phase/counters, validation acceptance/rejection, cancellation and unexpected exception type. At most 128 events are retained per lease; `omittedDiagnosticCount` records overflow. Existing requests, tool traces, evidence hashes and reported/unknown usage remain available.
- Failed validation records `validationRule` and `validationDetails`: branch, JSON path/index and applicable evidence IDs/kinds/availability. Hypothesis IDs and unauthorized references are represented by SHA-256 hashes rather than rejected model text. `advice-hypothesis-basis` distinguishes `overlapping-support-opposition` from `no-observational-support`.
- Successfully parsed model completions are saved **before business validation and usage reconciliation** in a separate private response store. This includes answers that later fail contract validation or exceed reported usage limits, and model-selected tool calls. Custom gateways are captured by the engine fallback; duplicate captures from the HTTP gateway and engine are suppressed.
- Each private file contains its format version, run/lease IDs, round, cumulative attempt number, request hash, receive time and exact parsed completion (content, tool calls, model and usage). Match the request hash to the run's existing request snapshots and use the matching frozen evidence for offline validation. A capture is diagnostic evidence, not an accepted investigation result.

Raw content is **not** included in the public run record, exception messages or ordinary logs. There is no response-download API and no link through the document store. Request bodies, authorization headers, connection strings and upstream error bodies are not captured. Protocol failures before a parsed completion exists retain stage/counter/failure metadata, not a raw HTTP/SSE recording. Full SSE bytes and sensitive provider headers are deliberately excluded.

`model-response` events expose only content size/hash, tool-call count, reported usage, capture status and an opaque filename. Capture statuses include `Stored`, `Disabled`, `NotConfigured`, `SizeLimit` and `WriteFailed`. Fake-provider service runs do not write response files. Capture failure does not change the investigation's business outcome. A missing or expired capture cannot be reconstructed from hashes.

## Storage and retention

| Setting | Default | Bounds / behavior |
| --- | --- | --- |
| `AI:Diagnostics:CaptureResponses` | `true` | Set `false` to stop new private captures; old files are still pruned. |
| `AI:Diagnostics:StorageRoot` | User-local `PlantOps/investigation-diagnostics` | Absolute, dedicated, operator-only directory. Never use a public/static/document directory. |
| `AI:Diagnostics:RetentionDays` | `7` | Clamped to 1–30 days, using file last-write time. |
| `AI:Diagnostics:MaxCaptureBytes` | `262144` | Serialized file limit, clamped to 1 KiB–1 MiB; oversized captures are omitted explicitly. |
| `AI:Diagnostics:MaxTotalBytes` | `104857600` | Clamped to 1 MiB–1 GiB; oldest captures are evicted first. |
| `AI:Diagnostics:MaxFiles` | `1000` | Clamped to 10–10000; oldest captures are evicted first. |

The worker prunes on startup and hourly, and the store prunes after each successful write. Only direct `response-*.json` files in its dedicated root are managed; unrelated files and symbolic links are skipped. Age limits are enforced when the worker is running; stopped workers cannot run cleanup. Retention applies to private payloads, not existing business run records or the external Docker log driver's retention policy.

Root Docker Compose uses a dedicated `investigation-diagnostics` volume mounted **only into the worker** at `/var/lib/plantops/investigation-diagnostics`. The backend image provisions an application-owned private directory; Unix directories/files use owner-only permissions. Windows uses the service account's local profile permissions; operators must restrict ACLs on any custom root. Response files contain potentially sensitive business/model content and require the same access discipline as the frozen evidence. Do not commit or copy them into public artifacts.

Compose overrides: `AI_DIAGNOSTICS_CAPTURE_RESPONSES`, `AI_DIAGNOSTICS_RETENTION_DAYS`, `AI_DIAGNOSTICS_MAX_TOTAL_BYTES`. Other settings can be supplied through normal .NET configuration, using double underscores for environment variables. Alternative deployments must mount their own private persistent directory and set `AI__Diagnostics__StorageRoot`; otherwise the local filesystem may be ephemeral.

## Investigation workflow

1. Locate the failed run and lease by ID/time. Read `failureCode` and the diagnostic timeline, not the UI's generic message alone.
2. For transport/protocol/budget errors, examine the recorded stage, HTTP status, retry decisions, stream phase and reported versus unknown usage.
3. For validation failures, read the specific rule, branch and item path. Find the private response capture using run/lease IDs and verify its request hash against the request snapshot. The content hash in the timeline verifies the exact answer text.
4. Replay the captured completion against the matching frozen evidence with the contract validator, without dispatching another model request. Keep accepted/rejected replay results separate from the original run.

This does not recover responses from older failures that never saved them. Changes require a new build/deployment before running containers emit these diagnostics.

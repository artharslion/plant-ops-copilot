# Longer bounded streaming investigations

**Goal:** Allow slow relay completions to finish within explicit limits and receive SSE responses, while showing truthful waiting/collection progress.

**Architecture:** Default request120/run180 seconds, maximum180; align leased worker lifetime and finite script wait. Optional StreamResponses setting retains non-stream compatibility. Bounded incremental SSE parser assembles text/tool deltas and usage, requires complete finish/DONE, then reuses existing response validation. Never show incomplete unvalidated model facts. Existing polling UI shows elapsed time and collected evidence count; this is progress, not live model prose.

**Steps:** Implement options/deadline/lease and SSE reader; test fragmented text/tools, usage, missing DONE, malformed/oversized/refused/truncated stream and cancellation. Run full isolated investigation regression and frontend checks. Commit verified stage. Run one fresh finite real investigation with streaming enabled,120/180 seconds, zero retries; preserve failure, examine returned actual usage and cited minute facts. Document and commit results. Never modify private settings, business DB, or old reports.

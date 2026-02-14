# Project Journal

Learned patterns, preferences, and project intelligence. Update when discovering something that helps future work.

## Entries

### 2026-02-14 - Heartbeat Visual Semantics

- Updated telemetry heartbeat glyph behavior in plugin settings: idle heart is near-transparent, successful heartbeat animates opacity up/down, error state shows broken heart, and recovery restores normal heart rendering.
- Build + deploy workflow completed successfully after the UI change.

### 2026-02-14 - Grafana OTLP Endpoint Compatibility

- Root cause for Grafana "auth" failures: plugin was posting Loki push payload (`streams`) to OTLP endpoint (`/otlp/v1/logs`) which expects OTLP log payload (`resourceLogs`).
- Updated exporter to auto-detect OTLP endpoint paths and switch payload format accordingly while keeping Basic auth behavior (username+api key or pre-encoded token).
- Connection semantics remain heartbeat-based; UI language now says "Connect telemetry" to avoid misleading "Authenticate" framing.
- Required deploy workflow executed via `plugin/scripts/deploy-plugin.ps1`; build and deployment verification passed.

### 2026-02-13 – Scaffold Review-Fix Closure

- Applied post-review fixes for scaffold implementation: SimHub SDK attribute/interface compatibility, deploy-script output path resolution, SDK link script privilege fallback, and agent rule-path correctness.
- Re-ran required dual review path (`simhub-developer` + `code-reviewer`) and both returned PASS.
- Remaining completion gate is manual runtime validation inside SimHub (build/deploy/load/telemetry confirmation).

### 2026-02-13 – Execution Workflow Tightening

- User preference: after each completed plugin change iteration, run `plugin/scripts/deploy-plugin.ps1` and report the result.

### 2026-02-13 – Product Vision Reset

- Sim Steward pivoted from AI-powered incident analyzer to incident clipping tool. Core loop: detect -> jump replay -> OBS records clip -> save.
- Purged all old-era artifacts: backend (Cloudflare Worker, R2, Workers AI), monetization (Whop, Statsig gates), telemetry CSV pipeline, web platform, driver reputation system.
- New PRD v2.0 with 15 FR-IDs across two parts. 10 user stories written (1 scaffold + 6 Part 1 + 3 Part 2).
- No backend, no AI, no monetization in current scope. AI analysis explicitly deferred to future phase.

### 2026-02-11 – Subagent Orchestration

- Cursor delegates via subagent descriptions; "use proactively" is the key signal
- delegation.mdc (alwaysApply) handles routing; keep it under 50 lines
- Model preference matters: code models for implementation agents, reasoning models for compliance/documentation

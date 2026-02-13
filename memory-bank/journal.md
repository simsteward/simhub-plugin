# Project Journal

Learned patterns, preferences, and project intelligence. Update when discovering something that helps future work.

## Entries

### 2026-02-13 – Product Vision Reset

- Sim Steward pivoted from AI-powered incident analyzer to incident clipping tool. Core loop: detect -> jump replay -> OBS records clip -> save.
- Purged all old-era artifacts: backend (Cloudflare Worker, R2, Workers AI), monetization (Whop, Statsig gates), telemetry CSV pipeline, web platform, driver reputation system.
- New PRD v2.0 with 15 FR-IDs across two parts. 10 user stories written (1 scaffold + 6 Part 1 + 3 Part 2).
- No backend, no AI, no monetization in current scope. AI analysis explicitly deferred to future phase.

### 2026-02-11 – Subagent Orchestration

- Cursor delegates via subagent descriptions; "use proactively" is the key signal
- delegation.mdc (alwaysApply) handles routing; keep it under 50 lines
- Model preference matters: code models for implementation agents, reasoning models for compliance/documentation

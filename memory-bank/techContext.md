# Tech Context

## Client (SimHub Plugin)

| Item | Value |
|------|-------|
| Framework | .NET Framework 4.8 |
| Libraries | SimHub.Plugins, iRacingSdkWrapper, Newtonsoft.Json |
| Location | `plugin/` |

## Backend (Cloudflare)

| Item | Value |
|------|-------|
| Compute | Cloudflare Workers |
| Storage | R2 (telemetry archive) |
| AI | Workers AI (Llama-3-8b-instruct or similar) |
| Auth (Beta) | Whop API + KV cache (1h) |

## MCP Servers

Cloudflare, GitHub, Statsig – use when relevant for infra, issues, feature flags.

## Cursor Orchestration

| Subagent | Purpose | Model Preference |
|---|---|---|
| simhub-developer | Plugin/C# implementation | Code model |
| cloudflare-worker | Worker/R2/AI backend | Code model |
| priority-steward | Priority tracking | Default |
| code-reviewer | PR/diff review | Code model |
| prd-compliance | FR-ID tracing, flag adjudication | Reasoning model |
| statsig-feature-ops | Alpha gates via Statsig MCP | Default |
| memory-bank-updater | Memory bank maintenance | Reasoning model |

Delegation is description-driven via `.cursor/rules/delegation.mdc`. Composer routes automatically based on intent.

## Constraints

- iRacing SDK: Cannot cut .rpy; user must record video manually if needed
- Whop dependency (Beta): API down = new users can't activate

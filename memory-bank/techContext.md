# Tech Context

## Client (SimHub Plugin)

| Item | Value |
|------|-------|
| Framework | .NET Framework 4.8 |
| Libraries | SimHub.Plugins, iRacingSdkWrapper, Newtonsoft.Json |
| Location | `plugin/` |
| Plugin Tab UI | HTML-rendered inside SimHub desktop shell (FR-A-012) |
| In-Game Overlay | SimHub overlay system for transparent HUD (FR-A-013) |
| Property System | Plugin exposes data as SimHub properties for UI binding |

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

8 subagents auto-routed via `.cursor/rules/delegation.mdc`. See that file for the routing table and model preferences.

## Constraints

- iRacing SDK: Cannot cut .rpy; user must record video manually if needed
- Whop dependency (Beta): API down = new users can't activate

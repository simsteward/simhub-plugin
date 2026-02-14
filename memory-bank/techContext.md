# Tech Context

## SimHub Plugin (Client -- Everything Runs Locally)

| Item | Value |
|------|-------|
| Framework | .NET Framework 4.8 |
| Libraries | SimHub.Plugins, iRacingSdkWrapper (bundled with SimHub), Newtonsoft.Json |
| Location | `plugin/` |
| Settings UI | WPF inside SimHub desktop shell (FR-008) |
| In-Game Overlay | SimHub Dash Studio for transparent HUD (FR-003, FR-015) |
| Property System | Plugin exposes data as SimHub properties for UI binding |

## iRacing SDK Integration

| Item | Value |
|------|-------|
| Library | iRSDKSharp.dll (ships with SimHub) |
| Incident detection | `PlayerCarTeamIncidentCount` delta monitoring |
| Replay jump | `irsdk_BroadcastReplaySearchSessionTime` via low-level `sdk.BroadcastMessage()` |
| Camera control (Part 2) | `irsdk_broadcastMsg` `CamSwitchNum` |
| Camera enumeration (Part 2) | Session info YAML parsing -- **spike needed** |
| Details | `docs/tech/sdk-investigation.md` |

## OBS Integration

| Item | Value |
|------|-------|
| Protocol | obs-websocket 5.x (built into OBS since v28) |
| Transport | WebSocket client in plugin |
| Operations | Connect, start/stop recording, get recording status |
| Risk | .NET 4.8 WebSocket client stability -- **spike needed** |

## No Backend

There is no server component. No Cloudflare, no R2, no Workers AI, no API. Everything runs on the user's machine.

## MCP Servers

- **GitHub** — issues and PRs.
- **Grafana** — https://wgutmann.grafana.net; create/query dashboards, folders, Loki. See `docs/tech/grafana-mcp.md`.

## Cursor Orchestration

Subagents auto-routed via `.cursor/rules/delegation.mdc`. See that file for routing table and model preferences. Active agents: simhub-developer, product-owner, priority-steward, code-reviewer, prd-compliance, memory-bank-updater.

## Constraints

- OBS must be running for recording features to work
- iRacing SDK cannot cut .rpy files; recording is via OBS screen capture
- .NET 4.8 limits modern WebSocket library options (no System.Net.WebSockets.Client without workarounds)
- SimHub free license = 10Hz DataUpdate; licensed = up to 60Hz
- Video stitching (Part 2) approach TBD -- FFmpeg CLI, OBS scene switching, or deliver separate files

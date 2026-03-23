# Sim Steward

A **SimHub plugin + browser dashboard** for structured iRacing replay review. Instead of manually scrubbing a replay to find incidents, Sim Steward gives you an incident queue, one-click frame seeks, a driver-scoped view, and automated scan walks — all observable via Grafana Loki.

---

## What it does today

| Area | Shipped |
|------|---------|
| **Replay controls** | Jump start/end, speed pills (0.25×–16×), play/pause, prev/next incident |
| **Incident leaderboard** | All incidents from plugin, filter by severity (1×/2×/4×/Mine), click to seek |
| **Incident meta strip** | Click any incident → replay seeks to frame + detail panel expands (car/driver/sev/cause) |
| **This driver's incidents** | Left-column panel filtered by selected car — for reviewing any opponent, not just yourself |
| **Scan walk** | Auto-seek every incident frame for one driver or the whole session; results in Captured tab |
| **Captured incidents** | Visited incidents list with group-by-driver accordion |
| **Driver standings** | Position/car/driver/incident count, collapsible |
| **Telemetry strip** | Throttle, brake, steering wheel (real data from plugin) |
| **Selected Incident Panel** | Camera group dropdown (`cameraGroups` from plugin), ▶ Capture (`capture_incident`: pre-roll, optional camera, 1× speed), prev/next within filtered list |
| **Observability** | Structured logs → Grafana Loki (`SIMSTEWARD_LOKI_URL`); `capture_incident` includes correlation fields on `action_result`; re-capture confirms before sending (Loki is append-only) |
| **Replay incident index (iRacing replay)** | WebSocket actions `replay_incident_index_build` (`start` / `cancel`), `replay_incident_index_seek` (JSON `sessionTimeMs`, optional `sessionNum`), `replay_incident_index_record` (`on` / `off` — 60Hz NDJSON under `%LocalAppData%\SimSteward\replay-incident-index\record-samples\`). IRSDKSharper 60Hz poll, 16× fast-forward, detection → JSON index on disk (`...\{subSessionId}.json`, TR-020 v1). **Dashboard:** `http://<host>:8888/Web/sim-steward-dash/replay-incident-index.html` (summary, sortable table, build/record, seeks); main dash links to it. Spec: [docs/IRACING-REPLAY-INCIDENT-INDEX-REQUIREMENTS.md](docs/IRACING-REPLAY-INCIDENT-INDEX-REQUIREMENTS.md). |

**North-star / gaps still open:** true plugin-side **YAML scan** (session walk still uses the leaderboard frame list), **scrub bar** seek (PoC / toast only), **plugin-owned `suggestedCamera`**, **dual-view** capture, **OBS** integration. See [docs/PRODUCT-FLOW.md](docs/PRODUCT-FLOW.md) and [docs/DATA-ROUTING-OBSERVABILITY.md](docs/DATA-ROUTING-OBSERVABILITY.md) for what belongs in Loki vs a future metrics path.

---

## Architecture

```
iRacing (shared memory)
    │
    ▼
SimSteward.Plugin (C# / .NET 4.8 / SimHub)
    │  IRSDKSharper reads iRacing SDK
    │  Fleck WebSocket server → port 19847
    │  PluginLogger → plugin-structured.jsonl
    │
    ├──→ Browser dashboard (HTML/JS)
    │         src/SimSteward.Dashboard/index.html
    │         served by SimHub HTTP server
    │
    └──→ Grafana Loki (optional)
              plugin → HTTPS POST to SIMSTEWARD_LOKI_URL (single endpoint)
              local Docker stack: observability/local/
```

---

## Repository layout

```
src/
  SimSteward.Plugin/          C# SimHub plugin (.NET 4.8)
  SimSteward.Dashboard/       Browser dashboard (index.html, no build step)
  SimSteward.Plugin.Tests/    xUnit unit tests

docs/                         Documentation (start with docs/README.md)
  PRODUCT-FLOW.md             Vision, feature maturity, what's missing
  USER-FLOWS.md               Step-by-step user journeys + flow diagrams
  USER-FEATURES-PM.md         PM-style feature descriptions
  GRAFANA-LOGGING.md          Loki labels, events, LogQL
  IRACING-REPLAY-INCIDENT-INDEX-REQUIREMENTS.md  SDK fast-forward incident index (milestones, TR IDs)
  DATA-ROUTING-OBSERVABILITY.md  Events vs high-rate telemetry (Loki vs OTel/metrics)
  TROUBLESHOOTING.md          Runtime issues, deploy, logs

observability/local/          Local Grafana + Loki Docker stack
tests/                        PowerShell integration tests
scripts/                      obs-bridge, Loki helpers, deploy utilities
deploy.ps1                    Build + deploy to local SimHub
```

---

## Getting started

### Prerequisites

- [SimHub](https://www.simhubdash.com/) installed
- iRacing
- .NET Framework 4.8 SDK
- Place `SimHub.Plugins.dll` and `GameReaderCommon.dll` in `lib/SimHub/` (or set `$env:SIMHUB_PATH`)

### Deploy

```powershell
.\deploy.ps1
```

Builds the plugin, runs `dotnet test`, runs PowerShell integration tests, then copies DLLs to the SimHub plugins folder. Requires 0 build errors and all tests passing.

### Open the dashboard

With SimHub running and the plugin loaded, open:

```
http://localhost:<SimHub-HTTP-port>/pages/sim-steward-dash/index.html
```

The dashboard connects to the plugin WebSocket on port `19847` automatically.

### Local observability (optional)

```powershell
.\scripts\run-simhub-local-observability.ps1
```

Starts Grafana + Loki via Docker. See [docs/observability-local.md](docs/observability-local.md).

---

## Key docs

| Doc | Read when |
|-----|-----------|
| [docs/README.md](docs/README.md) | Start here — tiered index of all docs |
| [docs/PRODUCT-FLOW.md](docs/PRODUCT-FLOW.md) | Understanding the vision, feature maturity, open PM issues |
| [docs/USER-FLOWS.md](docs/USER-FLOWS.md) | How each feature actually works as a user (flow diagrams) |
| [docs/GRAFANA-LOGGING.md](docs/GRAFANA-LOGGING.md) | Structured logging, Loki labels, LogQL queries |
| [docs/IRACING-REPLAY-INCIDENT-INDEX-REQUIREMENTS.md](docs/IRACING-REPLAY-INCIDENT-INDEX-REQUIREMENTS.md) | Replay incident index build (SDK-only), JSON output, validation, milestone status |
| [docs/DATA-ROUTING-OBSERVABILITY.md](docs/DATA-ROUTING-OBSERVABILITY.md) | What ships to Loki vs metrics at scale |
| [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) | Runtime issues, WebSocket, deploy failures |

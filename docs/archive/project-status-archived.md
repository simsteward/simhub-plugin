# Sim Steward — Project Status (archived copy)

> Snapshot as of 2026-03-04. **Current** snapshot: **docs/STATUS.md**. Do not treat this file as up to date.

---

## Current Status (2026-03-04)

All incident detection bugs are fixed. Grafana Loki structured logging is implemented and documented. YAML incident detection uses `CurDriverIncidentCount` (correct data source, populated on first YAML update). The data contract for OBS integration is locked.
Runtime verification (deploy → replay → confirm incidents fire) is the only open step before OBS work begins.

---

## What Was Built

### LAN Access Fix
- Fleck WebSocket default bind changed `127.0.0.1` → `0.0.0.0`.
- Dashboard `127.0.0.1` fallback only activates for localhost connections, not LAN.
- `deploy.ps1` writes `SimHub\Web\index.html` (meta-refresh to `/Web/sim-steward-dash/`) and prints LAN URLs.
- Skills and `.cursor/rules` updated.

### Data Reliability Hardening — Priority 1: Correctness
- Seek-backward threshold: 10 frames / 1 s (was 60 frames / 5 s). Frame number is primary ground truth.
- **YAML-only**: All incident detection is from session YAML `DriverInfo.Drivers[].CurDriverIncidentCount`. Non-admin live race produces zero other-driver YAML events by design; diagnostic log and post-checkered behaviour documented.

### Data Reliability Hardening — Priority 2: Data Enrichment
- Dashboard toast on `incident_not_found` from `SelectIncidentAndSeek`.
- **Incident cause**: Inferred from `InferYamlCause` using car proximity at YAML fire time.

### Data Reliability Hardening — Priority 3: Operational Reliability
- YAML fallback logs a warning when falling back to last-session heuristic.
- Init log message corrected from "skeleton" wording.

### Data Reliability Hardening — Priority 4: OBS Readiness (contracts only)
- `replayFrameNum` on `IncidentEvent` — exact frame for OBS to seek before starting a clip.
- `sessionId` in state JSON: `{trackName}_{yyyyMMdd}` for stable clip naming.
- `INTERFACE.md` fully updated (§1 bind, §3.2 new fields, §5 `sessionId`, §7 hosting model).

### Checkered-flag session summary
- On first tick with `SessionState >= 5` (checkered/cool-down), plugin broadcasts `sessionComplete` (summary without incidentFeed). Summary includes full results table and plugin incident feed for validation.

### Grafana Loki structured logging
- Plugin pushes structured logs to Grafana Loki (Grafana Cloud or local Docker) via `LokiSink`. Event-driven only; 4-label schema; `SessionStats` and `session_digest` for AI-friendly session summaries. Provisioned dashboards: Command Audit, Incident Timeline, Plugin Health, Session Overview. See **docs/GRAFANA-LOGGING.md**.

---

## Open Tasks

| # | Task | Status |
|---|------|--------|
| 1 | Runtime verification: deploy → iRacing replay → confirm incidents fire | done |
| 2 | OBS integration: scene switching + clip recording from `incidentEvents` WebSocket | done (bridge provided) |

---

## OBS Integration

A **Node.js bridge** is provided: `scripts/obs-bridge/`. It connects to the Sim Steward Fleck WebSocket and OBS WebSocket (obs-websocket 5.x). On each `incidentEvents` push it:

1. Connects to the Fleck WebSocket (`ws://<host>:19847`).
2. Optionally sends `ReplaySeekFrame` so iRacing seeks to the incident frame before recording.
3. Calls OBS `StartRecord`. Suggested clip naming: `{sessionId}_inc_{replayFrameNum}.mkv` (see bridge README for OBS filename options).

Run: `cd scripts/obs-bridge && npm install && npm start`. See `scripts/obs-bridge/README.md` for environment variables and requirements.

---

## Key File Map

| File | Purpose |
|------|---------|
| `src/SimSteward.Plugin/IncidentTracker.cs` | YAML incident detection + replay scan state machine |
| `src/SimSteward.Plugin/SimStewardPlugin.cs` | Plugin entry point, `DataUpdate` wiring, Loki/logging wiring |
| `src/SimSteward.Plugin/PluginState.cs` | Snapshot + diagnostics data model (WebSocket state: PluginSnapshot, DetectionMetrics, PluginDiagnostics, ProjectMarkers, SessionSummary) |
| `src/SimSteward.Plugin/PluginLogger.cs` | File + ring-buffer logger; `Structured()` / `Debug()` / `Emit()`; `LogWritten` → dashboard + LokiSink |
| `src/SimSteward.Plugin/LokiSink.cs` | Batches `LogEntry` and pushes to Loki (env: `SIMSTEWARD_LOKI_*`, `SIMSTEWARD_LOG_ENV`) |
| `src/SimSteward.Plugin/SessionStats.cs` | Per-session accumulator for `session_digest` (actions, latencies, incidents, errors); reset on iRacing connect |
| `src/SimSteward.Dashboard/index.html` | Single-file HTML/JS dashboard |
| `scripts/obs-bridge/` | OBS integration: Node.js bridge (Sim Steward WS → OBS WebSocket 5); run `npm install && npm start` |
| `observability/local/` | Local Loki + Grafana Docker stack; provisioned dashboards |
| `docs/INTERFACE.md` | Plugin ↔ dashboard WebSocket contract |
| `docs/GRAFANA-LOGGING.md` | Grafana Loki logging: schema, events, LogQL, dashboards, local/cloud config |

**Note:** "Memory bank" (file-based state sync, MCP tools, `memory-bank/` directory) is **not** a project feature. It is only for the developer's personal Cursor/vibe coding and must not be considered a feature, task, or item of reference. It is expected to be missing from the codebase.

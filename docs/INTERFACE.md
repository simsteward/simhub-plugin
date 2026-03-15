# Sim Steward Plugin — Dashboard Interface Contract

This document is the single source of truth for communication between the Sim Steward SimHub plugin and the HTML dashboard. The dashboard is built separately and consumes only this contract.

---

## 1. WebSocket Server

| Item | Value |
|------|--------|
| Library | Fleck (TcpListener-based; no Windows admin required) |
| URL | `ws://{host}:{port}` (default host = machine's own address) |
| Port | Configurable via `SIMSTEWARD_WS_PORT`; default **19847**. |
| Bind address | Configurable via `SIMSTEWARD_WS_BIND`; default **0.0.0.0** (all interfaces — required for LAN access). Set to `127.0.0.1` to restrict to localhost only. |
| Token | Optional `SIMSTEWARD_WS_TOKEN`; when set the dashboard must connect using `?token=<value>` (case-sensitive). |
| Origin | No restrictions (Fleck default) |

---

## 2. Connection Lifecycle

1. **Dashboard** opens `new WebSocket("ws://host:19847")`. When `SIMSTEWARD_WS_BIND`, `SIMSTEWARD_WS_PORT`, or `SIMSTEWARD_WS_TOKEN` are overridden, use the configured host/port and append `?token=<value>` when a token is present (the plugin closes unauthenticated connections immediately).
2. **Plugin** `OnOpen`: adds client to list and **immediately** pushes (a) current full state (`type: "state"`) and (b) recent log tail (`type: "logEvents"`, last 50 entries) so late-joining clients have context.
3. **Plugin** `DataUpdate` (throttled ~200 ms): broadcasts state to all connected clients.
4. **Dashboard** `onmessage`: parses JSON, updates UI from payload (discriminate by `type`).
5. **Dashboard** sends `{ "action": "ActionName", "arg": "..." }` when user interacts.
6. **Plugin** `OnMessage`: dispatches action, sends `{ type: "actionResult", ... }` back on the **same** socket.
7. **Plugin** `OnClose`: removes client from list.
8. **Dashboard** `onclose`: reconnects (e.g. after 2 s, exponential backoff up to 30 s).

---

## 3. Message Types: Plugin → Dashboard

All messages are JSON. The `type` field discriminates.

| `type` | When sent | Shape |
|--------|-----------|--------|
| `state` | On connect + every ~200 ms | Full state object; see §5. |
| `logEvents` | On connect (tail) + after every log write | `{ "type": "logEvents", "entries": [ LogEntry, … ] }` |
| `incidentEvents` | When new incidents are detected (push) | `{ "type": "incidentEvents", "events": [ IncidentEvent, … ] }` |
| `physicsEvents` | When player-car physics signals fire | `{ "type": "physicsEvents", "events": [ PhysicsEvent, … ] }` |
| `actionResult` | After receiving an action from this client | `{ "type": "actionResult", "action": "<string>", "success": <bool>, "result": "<string>?", "error": "<string>?" }` |
| `pong` | After receiving `ping` action | `{ "type": "pong" }` |
| `error` | Malformed message or missing action | `{ "type": "error", "error": "<string>" }` |

### 3.1 `logEvents` Schema

Sent immediately on client connect (last 50 entries as backfill) and then once per log write. Each entry is a structured `LogEntry`; the plugin also ships these to Loki when `SIMSTEWARD_LOKI_URL` is set (see **docs/GRAFANA-LOGGING.md**).

```json
{
  "type": "logEvents",
  "entries": [
    {
      "level":     "INFO",
      "message":   "DashboardBridge: WebSocket listening on 127.0.0.1:19847 (token not required)",
      "timestamp": "2026-02-27T14:05:01.234Z",
      "component": "bridge",
      "event":     "ws_client_connected",
      "fields":    { "client_ip": "127.0.0.1", "client_count": 1 }
    }
  ]
}
```

| Field | Type | Values / notes |
|-------|------|----------------|
| `level` | string | `"INFO"` \| `"WARN"` \| `"ERROR"` \| `"DEBUG"` (DEBUG only when `SIMSTEWARD_LOG_DEBUG=1`) |
| `message` | string | Log line text |
| `timestamp` | string | ISO-8601 UTC (`yyyy-MM-ddTHH:mm:ss.fffZ`) |
| `component` | string (optional) | `"simhub-plugin"` \| `"bridge"` \| `"tracker"` \| `"dashboard"` — present for structured events |
| `event` | string (optional) | Event type (e.g. `action_result`, `incident_detected`, `session_digest`) — see **docs/GRAFANA-LOGGING.md** |
| `fields` | object (optional) | Key-value payload for the event (e.g. `action`, `correlation_id`, `success`, `duration_ms`) |

### 3.2 `incidentEvents` Schema

Pushed immediately when `IncidentTracker` detects a new incident. May contain multiple events if several were detected in a single DataUpdate batch (e.g. high replay speed). Each event is also represented in the `incidents` array of the next `state` push.

```json
{
  "type": "incidentEvents",
  "events": [
    {
      "id":                   "a1b2c3d4",
      "sessionTime":          3723.5,
      "sessionTimeFormatted": "62:03",
      "carIdx":               5,
      "driverName":           "Jane Smith",
      "carNumber":            "42",
      "delta":                4,
      "totalAfter":           8,
      "type":                 "4x",
      "source":               "player",
      "cause":                "heavy-contact",
      "peakG":                4.2,
      "lap":                  12,
      "trackPct":             0.437,
      "otherCarIdx":          11,
      "otherCarNumber":       "17",
      "otherDriverName":      "John Doe"
    }
  ]
}
```

| Field | Type | Description |
|-------|------|-------------|
| `id` | string | Short unique ID (8 hex chars). Use with `SelectIncidentAndSeek`. |
| `sessionTime` | number | Session clock seconds at incident time. |
| `sessionTimeFormatted` | string | `M:SS` formatted time. |
| `carIdx` | number | iRacing car index. |
| `driverName` | string | Driver's display name. |
| `carNumber` | string | Car number string. |
| `delta` | number | Incident points added this event. Standard values: 1, 2, or 4. 0 = physics-detected, no official count change (0x). |
| `totalAfter` | number | Driver's running total after this event. |
| `type` | string | `"1x"` \| `"2x"` \| `"4x"` \| `"batched"` \| `"detected"` \| `"0x"` — `"batched"` means YAML flushed multiple incidents together; `"detected"` means physics saw an event before YAML confirmed it. |
| `source` | string | `"player"` (60 Hz telemetry, exact) \| `"yaml"` (session YAML, all-driver, may batch) \| `"physics"` (Layer 2/3 detected, no official count change yet) |
| `cause` | string or null | Physics-inferred cause: `"off-track"` \| `"wall"` \| `"wall-or-spin"` \| `"spin"` \| `"car-contact"` \| `"heavy-contact"` \| `"impact"` \| `null` |
| `peakG` | number | Peak combined G-force (m/s² / 9.81) at the incident frame. 0 if not available. |
| `lap` | number | Lap number at incident time (player incidents only; 0 for others). |
| `trackPct` | number | Track position as fraction of lap (0–1) at incident time. |
| `otherCarIdx` | number | CarIdx of the other car involved in contact (-1 if none or unknown). |
| `otherCarNumber` | string or null | Car number of the other car (-1 → omit display). |
| `otherDriverName` | string or null | Driver name of the other car. |
| `replayFrameNum` | number | iRacing replay frame at incident time. OBS uses this to seek to the exact frame before starting a clip. 0 if unavailable. |
| `physicsCause` | string or null | Cause mapped from the best-matching `physicsEvents` signal within 2 s of the incident (null if no match). |
| `physicsEventId` | string or null | `id` of the correlated `PhysicsEvent` (null if no match). |
| `causeConfidence` | string | `"high"` — physics correlation matched; `"medium"` — telemetry threshold only; `"low"` — YAML-only, no physics data. |

**Notes on `source: "yaml"` events:** At high replay speeds, iRacing batches multiple incidents into a single YAML flush. When the total delta does not match a standard 1/2/4 value, `type` is `"batched"` and `delta` is the total points added in the batch. The `cause` field reflects the best available physics inference for the batch window.

---

### 3.3 `physicsEvents` Schema

Sent when player-car physics signals exceed thresholds. These are observation-only events — they do not increment any incident count. Each event has a per-signal cooldown (2 s) to prevent spamming.

```json
{
  "type": "physicsEvents",
  "events": [
    {
      "id":                   "a1b2c3d4",
      "sessionTime":          3723.5,
      "sessionTimeFormatted": "62:03",
      "signal":               "LatAccelSpike",
      "value":                3.74,
      "threshold":            3.0,
      "lap":                  12,
      "trackPct":             0.437
    }
  ]
}
```

| Field | Type | Description |
|-------|------|-------------|
| `id` | string | Short unique ID (8 hex chars). |
| `sessionTime` | number | Session clock seconds when the signal fired. |
| `sessionTimeFormatted` | string | `M:SS` formatted time. |
| `signal` | string | Signal name — see table below. |
| `value` | number | Measured value at firing (units per-signal). |
| `threshold` | number | Threshold that was exceeded. |
| `lap` | number | Current lap number at signal time. |
| `trackPct` | number | Track position (0–1) at signal time. |

**Signal names:**

| Signal | Unit | Typical cause |
|--------|------|---------------|
| `LongAccelSpike` | g | Frontal impact (braking, wall/car ahead) |
| `LatAccelSpike` | g | Side impact (T-bone, barrier contact) |
| `VertAccelSpike` | g | Vertical shock (kerb, ramp, airborne landing) |
| `YawRateSpike` | rad/s | Spin onset (snap oversteer, loss of control) |
| `SuspensionImpact` | m/s | Hard suspension hit (max shock velocity across all four corners) |
| `TireLockup` | m/s | Brake lockup (wheel hub speed vs vehicle speed delta) |
| `TireSpinout` | m/s | Wheelspin (drive wheel speed vs vehicle speed delta) |

---

## 4. Message Types: Dashboard → Plugin

Dashboard sends JSON with at least:

- **`action`** (string, required): action name.
- **`arg`** (string, optional): argument for the action.

Special cases:

- **`ping`**: no `arg` needed; plugin replies with `{ "type": "pong" }`.
- **`log`**: body should include `level`, `message`, `source`; plugin appends to `plugin.log` with `[OVERLAY]` prefix.

### 4.1 Action Reference

| Action | `arg` | Status | Description |
|--------|--------|--------|-------------|
| `ping` | — | Implemented | Heartbeat; plugin replies `pong`. |
| `log` | — | Implemented | Body must include `level`, `message`, `source`. Plugin writes to plugin.log. |
| `ReplayPlayPause` | — | Implemented | Toggle replay play/pause. |
| `ReplaySetSpeed` | Signed speed string (e.g. `"-2"`, `"1"`, `"16"`) | Implemented | Set replay speed. |
| `NextIncident` | — | Implemented | Navigate to next incident (iRacing broadcast). |
| `PrevIncident` | — | Implemented | Navigate to previous incident (iRacing broadcast). |
| `ReplayStepFrame` | `"-1"` or `"1"` | Implemented | Step one frame backward/forward. |
| `ReplaySeekFrame` | Absolute frame index (string) | Implemented | Seek replay to absolute frame. |
| `SelectIncidentAndSeek` | incidentId (string) | Implemented | Look up incident by ID and seek replay to its session time. |
| `CaptureSessionSummaryNow` | — | Implemented | Attempt to capture and broadcast session summary *at the current state*. Fails with `results_not_ready` if `ResultsPositions` is not populated yet. |
| `FinalizeThenCaptureSessionSummary` | — | Implemented | Replay only: pause, seek to replay end to force final results emission, capture summary, then restore the previous replay position/speed. |
| `RecordSessionSnapshot` | — | Implemented | Append a JSON line to `PluginsData/SimSteward/session-discovery.jsonl` containing `sessionDiagnostics` + replay position. Use this while running the discovery scenarios. |
| `ToggleIntentionalCapture` | — | Stub | Start/stop capture. |
| `SetReplayCaptureSpeed` | `"1"` … `"64"` | Stub | Set capture speed. |
| `SetSecondsBefore` | `"0"` … `"120"` | Stub | Seconds before incident in capture window. |
| `SetSecondsAfter` | `"0"` … `"120"` | Stub | Seconds after incident in capture window. |
| `SetCaptureDriver1` | CarIdx as string | Stub | Set driver 1 for capture. |
| `SetCaptureDriver2` | CarIdx as string | Stub | Set driver 2 for capture. |
| `SetCaptureCamera1` | `"groupNum:cameraNum"` (e.g. `"4:1"`) | Stub | Set camera 1. |
| `SetCaptureCamera2` | `"groupNum:cameraNum"` | Stub | Set camera 2. |
| `SetAutoRotateAndCapture` | `"true"` or `"false"` | Stub | Enable/disable auto-rotate and capture. |
| `ToggleAutoRotateAndCapture` | — | Stub | Toggle auto-rotate. |
| `SetAutoRotateDwellSeconds` | `"0.5"` … `"30"` | Stub | Dwell time (seconds) per step in auto-rotate. |

---

## 5. State Schema (Plugin → Dashboard)

Every state message has `"type": "state"` and the following fields. All fields are present on every push.

| Field | Type | Description |
|-------|------|-------------|
| `type` | string | Always `"state"`. |
| `pluginMode` | string | `"Unknown"`, `"Live"`, or `"Replay"`. |
| `currentSessionTime` | number | Current session time (seconds). |
| `currentSessionTimeFormatted` | string | Session time as `M:SS`. |
| `replayIsPlaying` | boolean | Replay is playing. |
| `replayFrameNum` | number | Current replay frame (plugin maps from SDK `ReplayFrameNumEnd`). |
| `replayFrameNumEnd` | number | Last replay frame / timeline end (plugin maps from SDK `ReplayFrameNum`). |
| `replayPlaySpeed` | number | Current replay speed (signed; 0 = paused). |
| `replayPlaySlowMotion` | boolean | Slow-motion modifier. |
| `replaySessionNum` | number | iRacing session number currently being replayed (or current live session number). |
| `playerCarIdx` | number | CarIdx of the **focused** driver: in replay/spectator this is the car being watched (CamCarIdx), otherwise the car you're driving (DriverCarIdx). -1 if unknown. |
| `playerIncidentCount` | number | Player's total incident count. |
| `hasLiveIncidentData` | boolean | True once the YAML baseline has been established and incident deltas are being tracked. False until the first session YAML parse completes. |
| `trackName` | string | Track name from `WeekendInfo.TrackName` (empty string until YAML available). |
| `trackCategory` | string | Track category from `WeekendInfo.Category` (e.g. `"Road"`, `"Oval"`, `"DirtOval"`, `"DirtRoad"`). Defaults to `"Road"` until YAML available. |
| `trackLengthM` | number | Track length in metres derived from `WeekendInfo.TrackLength`. Used for G-force calculations. |
| `sessionId` | string | Stable session identifier: `"{trackName}_{yyyyMMdd}"`. OBS uses this to name clip files unambiguously. Empty string until YAML is available. |
| `drivers` | array | Driver roster. Each element: `{ carIdx, userName, carNumber, incidents, isPlayer, isSpectator }`. |
| `incidents` | array | Incident feed (newest first, max 200). See §3.2 for full field list. |
| `metrics` | object | Detection counters since last iRacing connection (Layer 4 YAML only). Fields: `l4YamlEvents`, `totalEvents`, `yamlUpdates`, `lastDetectionSessionTime`. |
| `diagnostics` | object | Plugin subsystem health. Fields: `irsdkStarted`, `irsdkConnected`, `wsRunning`, `wsPort`, `wsClients`, `playerCarIdx`. |
| `sessionDiagnostics` | object | Session/YAML readiness + last capture status (discovery instrumentation). Fields include: `simMode`, `irSessionId`, `irSubSessionId`, `sessionState`, `sessionNum`, `sessionInfoUpdate`, `sessionFlags`, `resultsPositionsCount`, `resultsReady`, incident non-zero stats, and `lastSummaryCapture`. |

### 5.1 Planned State Fields (capture feature — not yet implemented)

The following fields will be added when the video capture feature is built. They are **not** sent today.

| Field | Type | Description |
|-------|------|-------------|
| `hasMatchingEventFile` | boolean | True if a session document exists on disk. |
| `incidentDataRecommendation` | string | Human-readable recommendation when live data is missing. |
| `intentionalCaptureActive` | boolean | True when capture is running. |
| `eventFileJson` | string | JSON string of event file (participants + incidents). |
| `selectedIncidentId` | string | ID of currently selected incident. |
| `selectedIncidentSessionTime` | number | Session time (seconds) of selected incident. |
| `selectedIncidentReplayFrame` | number | Replay frame for selected incident (0 if none). |
| `selectedIncidentDriverName` | string | Driver name for selected incident. |
| `selectedIncidentCarNumber` | number | Car number for selected incident. |
| `selectedIncidentPointsDelta` | number | Incident points delta (1x, 2x, 4x). |
| `selectedIncidentType` | string | Classification (e.g. minor, moderate, hard-contact, severe). |
| `replayCaptureSpeed` | number | Capture speed (1–64). |
| `captureSessionStartTime` | number | Start of capture window (session time). |
| `captureSessionEndTime` | number | End of capture window (session time). |
| `secondsBeforeIncident` | number | Seconds before incident in capture. |
| `secondsAfterIncident` | number | Seconds after incident in capture. |
| `captureDriver1CarIdx` | number | CarIdx for driver 1 (-1 if unset). |
| `captureDriver2CarIdx` | number | CarIdx for driver 2 (-1 if unset). |
| `captureCamera1` | string | Camera 1 id `"groupNum:cameraNum"`. |
| `captureCamera2` | string | Camera 2 id. |
| `cameraListJson` | string | JSON array of camera options for dropdowns. |
| `autoRotateAndCapture` | boolean | Auto-rotate and capture enabled. |
| `autoRotateDwellSeconds` | number | Dwell time per step (seconds). |
| `incidentCountFromFile` | number | Total incidents in session file. |

---

## 6. Error Semantics

| Scenario | Response |
|----------|----------|
| Valid action, success | `{ "type": "actionResult", "action": "<name>", "success": true, "result": "ok" }` (or action-specific result string). |
| Valid action, failed | `{ "type": "actionResult", "action": "<name>", "success": false, "error": "<reason>" }`. |
| Unknown action | `{ "type": "actionResult", "action": "<name>", "success": false, "error": "unknown_action" }`. |
| Malformed JSON | `{ "type": "error", "error": "invalid_json" }`. |
| Missing `action` field | `{ "type": "error", "error": "missing_action" }`. |

---

## 7. Dashboard Hosting

The dashboard is a static `index.html` file served by SimHub's built-in HTTP server (port 8888). It is **not** a SimHub DashTemplate and the plugin does not host its own HTTP server. The dashboard uses `window.location.hostname` to connect to the plugin WebSocket, so it automatically targets the correct SimHub machine whether opened locally or from the LAN.

| Item | Value |
|------|--------|
| Deploy path | `SimHub\Web\sim-steward-dash\` (`index.html`, `README.txt`) |
| Local URL | `http://localhost:8888/Web/sim-steward-dash/index.html` |
| LAN URL | `http://<simhub-ip>:8888/Web/sim-steward-dash/index.html` |
| LAN shortcut | `http://<simhub-ip>:8888/Web/` redirects to dashboard |

**Important:** SimHub owns its root route (`http://<host>:8888/`); it cannot be overridden by files on disk. The dashboard must live under `/Web/`. The deploy script writes a meta-refresh redirect at `SimHub\Web\index.html` so that `/Web/` forwards automatically.

**How to show the dashboard in SimHub:** In Dash Studio, add a **Web Page** (or **Web View**) component and set the URL to `http://localhost:8888/Web/sim-steward-dash/index.html`. The overlay connects to `ws://<host>:19847` automatically. When you enable `SIMSTEWARD_WS_TOKEN`, append `?token=<value>` to the URL so the dashboard forwards it to the WebSocket handshake.

---

## 8. SimHub Properties (Dash Studio)

For native Dash Studio bindings (NCalc/Jint), the plugin exposes a subset as SimHub properties:

| Property | Type | Description |
|----------|------|-------------|
| `SimSteward.PluginMode` | string | `"Unknown"`, `"Live"`, `"Replay"`. |
| `SimSteward.IncidentCount` | int | Total incidents this session. |
| `SimSteward.HasLiveIncidentData` | bool | True once session YAML baseline established and incident deltas are live. |
| `SimSteward.IntentionalCaptureActive` | bool | Capture running. |
| `SimSteward.HasMatchingEventFile` | bool | Session document exists. |
| `SimSteward.ClientCount` | int | Number of connected WebSocket clients. |

The HTML dashboard uses **only** the WebSocket state; it does not read these properties.

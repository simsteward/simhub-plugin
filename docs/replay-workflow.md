# Replay capture workflow

MCP-driven procedure, **test checklist**, and **capturable datapoints** for replay/live snapshots.

---

## 1. Detect replay vs live

- **MCP:** `simsteward_health` / `simsteward_diagnostics` → **Plugin Mode** (Replay/Live) or **simMode** (replay/live).
- **WebSocket state:** `pluginMode`, `simMode`.

If not replay: record snapshots at current time only; no seek/speed workflow.

---

## 2. Sessions list

Read `sessions[]` from diagnostics/state: `sessionNum`, `sessionType`, `sessionName`.

---

## 3. Order of operations (replay)

1. Confirm Replay mode.
2. Read `sessions[]`.
3. For each session N: **ReplaySeekSessionStart** (arg N); optionally **RecordSessionSnapshot** `session_start:N`.
4. **ReplaySeekToSessionEnd**(N); **RecordSessionSnapshot** `session_end:N`; **CaptureSessionSummaryNow** when results ready.
5. Speeds: 16x / 8x / 1x with triggers `session_N_16x`, etc.
6. Two drivers: primary view then contact driver; triggers `primary_driver`, `contact_driver` / `driver_contact:<carIdx>`.

---

## 4. Trigger labels (RecordSessionSnapshot)

| Trigger | Meaning |
|---------|--------|
| `session_start:N` / `session_end:N` | Session boundaries |
| `primary_driver` / `contact_driver` / `driver_contact:<carIdx>` | Camera target |
| `session_N_16x` / `_8x` / `_1x` | Speed |
| `far_chase` | Standardized camera |

---

## 5. When checkered is not in clip

Seek to session/replay end; **FinalizeThenCaptureSessionSummary**; use Loki (`session_end_fingerprint`, etc.) to see what was captured.

**Data availability:** **docs/reference/SESSION-DATA-AVAILABILITY.md**.

---

## Test checklist

**Prereqs:** SimHub + plugin; replay loaded for replay tests; optional SimSteward MCP.

**Snapshots:** `%LOCALAPPDATA%\SimHubWpf\PluginsData\SimSteward\session-discovery.jsonl`

### Expected by step

| Step | Expect |
|------|--------|
| Detect | `pluginMode`, `simMode` present; replay → Replay/replay |
| Sessions | `sessions[]` with sessionNum, sessionType, sessionName |
| ReplaySeekSessionStart(N) | success; position at session start |
| ReplaySeekToSessionEnd(N) | success or session_not_found |
| RecordSessionSnapshot | New JSONL line; type `sessionSnapshot`; optional `replayMetadata` |
| ReplaySetSpeed | success |
| CaptureSessionSummaryNow | success when ResultsPositions ready |

### Snapshot payload (minimum)

`type`, `trigger`, `pluginMode`, `playerCarIdx`, replay frame/speed/session fields, `sessionDiagnostics`. With SessionInfo: `replayMetadata` (sessionID, roster, incidents, …).

### Test cases (manual)

1. Detect — modes present in replay scenario.  
2. Sessions — non-empty array when replay + SessionInfo.  
3. Seek start + snapshot — new line with session_start.  
4. Seek end + snapshot — session_end line; optional Loki fingerprint.  
5. Payload shape — `replayMetadata` when SessionInfo available.  
6. Speed + snapshot — trigger matches speed.

**Automation:** **tests/ReplayWorkflowTest.ps1** (when SimHub/replay available).

---

## Capturable datapoints

**Full SDK notes:** **docs/reference/IRACING-SDK-DATAPOINTS-RESEARCH.md**

### Per-car: player vs others

Full throttle/brake/clutch/speed/G-force: **player car only**. Other cars: **CarIdx*** arrays (lap, position, gear, RPM, steer, track surface, lap times). No throttle/brake/clutch for other cars.

### Session / roster (YAML)

WeekendInfo, DriverInfo.Drivers[], SessionInfo.Sessions[]. **ResultsPositions** when session finalized (checkered or seek to end).

### Already captured

RecordSessionSnapshot fields; throttled **state**; session summary / `session_end_datapoints_*` when ready.

### Two-driver telemetry block

Player: full inputs. Other car: CarIdx* only. Keep snapshot under ~8 KB — **docs/GRAFANA-LOGGING.md**.
